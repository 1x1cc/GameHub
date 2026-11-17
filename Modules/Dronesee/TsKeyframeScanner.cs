using System;
using System.Collections.Generic;

namespace GameHub.Modules.Dronesee
{
    /// <summary>
    /// 轻量 MPEG-TS 解析器（只解析到"够用"为止）。
    ///
    /// 存在的理由：
    ///   即时重放缓冲把 FFmpeg 输出的 TS 流按「到达上限就丢队头」的方式滚动淘汰。
    ///   旧实现按 64KB 读块直接丢，淘汰点可能落在 GOP 中间 —— 保存出来的片段
    ///   第一帧不是关键帧（甚至不是 TS 包边界），播放器从这种片段起播就可能黑屏。
    ///
    /// 本类在读取流的同时顺带记录：
    ///   1) 「关键帧访问单元（AU）起始」的 TS 包绝对偏移 —— 片段可以从这些位置安全起播；
    ///   2) 最近的 PAT / PMT 包（各 188 字节）—— 保存片段时垫在最前面，
    ///      让片段自带节目表，转封装/播放器立刻能解析出流结构。
    ///
    /// 只做字节解析、不依赖 WPF / FFmpeg，便于单独跑测试（见测试工程）。
    /// </summary>
    public sealed class TsKeyframeScanner
    {
        public const int PacketSize = 188;

        // 一次最多记住的新关键帧偏移（防止长时间运行后列表无限增长）
        private const int MaxPendingKeyframes = 8192;

        private readonly List<long> _newKeyframes = new();
        private readonly byte[] _carry = new byte[PacketSize];
        private int _carryCount;
        private long _carryOffset;          // _carry[0] 在流中的绝对偏移
        private long _absBase;              // 下一次 Push 的 data[0] 在流中的绝对偏移

        private int _pmtPid = -1;
        private int _videoPid = -1;

        // 当前「已见到 PES 起始、但还没判定是否关键帧」的 AU
        private long _pendingAuOffset = -1;
        private bool _pendingAuDecided;

        /// <summary>最近见过的 PAT 包（188 字节，含同步字节 0x47）。</summary>
        public byte[]? LastPat { get; private set; }

        /// <summary>最近见过的 PMT 包（188 字节）。</summary>
        public byte[]? LastPmt { get; private set; }

        /// <summary>解析出来的视频 PID（未解析到时为 -1）。</summary>
        public int VideoPid => _videoPid;

        /// <summary>已解析出的关键帧偏移总数（累计，仅用于日志）。</summary>
        public long KeyframeTotal { get; private set; }

        public void Reset()
        {
            _newKeyframes.Clear();
            _carryCount = 0;
            _carryOffset = 0;
            _absBase = 0;
            _pmtPid = -1;
            _videoPid = -1;
            _pendingAuOffset = -1;
            _pendingAuDecided = false;
            KeyframeTotal = 0;
            LastPat = null;
            LastPmt = null;
        }

        /// <summary>取走自上次调用以来新发现的关键帧偏移（调用方负责按环形缓冲窗口淘汰）。</summary>
        public List<long> TakeNewKeyframes()
        {
            if (_newKeyframes.Count == 0) return EmptyList;
            var copy = new List<long>(_newKeyframes);
            _newKeyframes.Clear();
            return copy;
        }

        private static readonly List<long> EmptyList = new(0);

        /// <summary>
        /// 喂入一段**连续**的流数据（必须与上次 Push 的字节首尾相接）。
        /// 按 188 字节整包解析，跨 Push 的半个包由内部 carry 缓存拼起来。
        /// </summary>
        public void Push(byte[] data, int count)
        {
            if (data == null || count <= 0) return;
            if (count > data.Length) count = data.Length;

            int pos = 0;

            // 1) 把上一次残留的半包补齐
            if (_carryCount > 0)
            {
                int need = PacketSize - _carryCount;
                int take = Math.Min(need, count);
                Buffer.BlockCopy(data, 0, _carry, _carryCount, take);
                _carryCount += take;
                pos = take;

                if (_carryCount == PacketSize)
                {
                    ProcessPacket(_carry, 0, _carryOffset);
                    _carryCount = 0;
                }
            }

            // 2) 整包处理（data[0] 对应绝对偏移 _absBase）
            int alignedEnd = pos + ((count - pos) / PacketSize) * PacketSize;
            for (int i = pos; i < alignedEnd; i += PacketSize)
                ProcessPacket(data, i, _absBase + i);

            // 3) 剩余不足一个包的字节留到下回
            int rest = count - alignedEnd;
            if (rest > 0)
            {
                Buffer.BlockCopy(data, alignedEnd, _carry, 0, rest);
                _carryCount = rest;
                _carryOffset = _absBase + alignedEnd;
            }

            _absBase += count;
        }

        private void AddKeyframe(long offset)
        {
            KeyframeTotal++;
            if (_newKeyframes.Count >= MaxPendingKeyframes) return;   // 消费不及时则丢弃，避免无限增长
            if (_newKeyframes.Count > 0 && _newKeyframes[^1] == offset) return;
            _newKeyframes.Add(offset);
        }

        private void ProcessPacket(byte[] buf, int idx, long absOffset)
        {
            if (buf[idx] != 0x47) return;   // 失步：FFmpeg 从流首就是对齐的，这里直接忽略异常包

            bool pusi = (buf[idx + 1] & 0x40) != 0;
            int pid = ((buf[idx + 1] & 0x1F) << 8) | buf[idx + 2];

            int pktEnd = idx + PacketSize;
            bool hasPayload = (buf[idx + 3] & 0x10) != 0;
            int payload = idx + 4;
            if ((buf[idx + 3] & 0x20) != 0)             // adaptation field 存在
            {
                int afLen = buf[idx + 4];
                payload = idx + 5 + afLen;
            }
            if (!hasPayload || payload >= pktEnd) return;

            if (pid == 0)                               // PAT
            {
                var copy = new byte[PacketSize];
                Buffer.BlockCopy(buf, idx, copy, 0, PacketSize);
                LastPat = copy;
                ParsePat(buf, payload, pktEnd, pusi);
                return;
            }

            if (pid == _pmtPid)
            {
                var copy = new byte[PacketSize];
                Buffer.BlockCopy(buf, idx, copy, 0, PacketSize);
                LastPmt = copy;
                ParsePmt(buf, payload, pktEnd, pusi);
                return;
            }

            if (pid != _videoPid || _videoPid < 0) return;

            if (pusi)
            {
                // 视频 PES 起始：这里就是「AU 起始」的候选位置
                _pendingAuOffset = absOffset;
                _pendingAuDecided = false;

                int es = SkipPesHeader(buf, payload, pktEnd);
                if (es >= 0)
                    DecideAu(buf, es, pktEnd, absOffset);
                return;
            }

            // 续包：AU 开头可能被切到第二个 TS 包（少见），只在这个窗口内继续判定
            if (_pendingAuOffset >= 0 && !_pendingAuDecided)
                DecideAu(buf, payload, pktEnd, _pendingAuOffset);
        }

        /// <summary>
        /// 判断这个 AU 是不是关键帧。
        /// 规则：按 NAL 顺序往下看，先遇到 SPS(7) / IDR(5) → 关键帧；
        ///       先遇到非 IDR 条带(1) → 不是关键帧（后面不再看）。
        /// 这样既能命中「AUD+SPS+PPS+IDR」的布局，也不会被码流里偶发的 00 00 01 骗到。
        /// </summary>
        private void DecideAu(byte[] buf, int start, int end, long auOffset)
        {
            for (int q = start; q + 3 < end; q++)
            {
                if (buf[q] != 0 || buf[q + 1] != 0 || buf[q + 2] != 1) continue;

                int type = buf[q + 3] & 0x1F;
                q += 3;

                if (type == 7 || type == 5)             // SPS / IDR
                {
                    if (!_pendingAuDecided) AddKeyframe(auOffset);
                    _pendingAuDecided = true;
                    return;
                }
                if (type == 1)                          // 非 IDR 条带：本 AU 不是关键帧
                {
                    _pendingAuDecided = true;
                    return;
                }
                // AUD(9) / PPS(8) / SEI(6) 等继续往后看
            }
        }

        /// <summary>跳过 PES 头，返回 ES 数据起点；失败返回 -1。</summary>
        private static int SkipPesHeader(byte[] buf, int payload, int pktEnd)
        {
            if (payload + 9 > pktEnd) return -1;
            if (buf[payload] != 0 || buf[payload + 1] != 0 || buf[payload + 2] != 1) return -1;

            int headerLen = buf[payload + 8];
            int es = payload + 9 + headerLen;
            return es < pktEnd ? es : -1;
        }

        private void ParsePat(byte[] buf, int payload, int pktEnd, bool pusi)
        {
            int p = payload;
            if (pusi)
            {
                int pointer = buf[p];
                p += 1 + pointer;
            }
            if (p + 8 > pktEnd) return;
            if (buf[p] != 0x00) return;                 // table_id = PAT

            int sectionLen = ((buf[p + 1] & 0x0F) << 8) | buf[p + 2];
            int end = Math.Min(p + 3 + sectionLen - 4, pktEnd);   // 去掉 CRC

            for (int q = p + 8; q + 4 <= end; q += 4)
            {
                int program = (buf[q] << 8) | buf[q + 1];
                int pid = ((buf[q + 2] & 0x1F) << 8) | buf[q + 3];
                if (program != 0 && pid != 0x1FFF)
                {
                    _pmtPid = pid;
                    return;
                }
            }
        }

        private void ParsePmt(byte[] buf, int payload, int pktEnd, bool pusi)
        {
            int p = payload;
            if (pusi)
            {
                int pointer = buf[p];
                p += 1 + pointer;
            }
            if (p + 12 > pktEnd) return;
            if (buf[p] != 0x02) return;                 // table_id = PMT

            int sectionLen = ((buf[p + 1] & 0x0F) << 8) | buf[p + 2];
            int end = Math.Min(p + 3 + sectionLen - 4, pktEnd);

            int programInfoLen = ((buf[p + 10] & 0x0F) << 8) | buf[p + 11];
            int q = p + 12 + programInfoLen;

            while (q + 5 <= end)
            {
                int streamType = buf[q];
                int esPid = ((buf[q + 1] & 0x1F) << 8) | buf[q + 2];
                int esInfoLen = ((buf[q + 3] & 0x0F) << 8) | buf[q + 4];

                if (streamType == 0x1B && _videoPid < 0)   // H.264
                    _videoPid = esPid;

                q += 5 + esInfoLen;
            }
        }
    }
}
