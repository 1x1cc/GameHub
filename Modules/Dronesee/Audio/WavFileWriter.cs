using System;
using System.IO;

namespace GameHub.Modules.Dronesee.Audio
{
    /// <summary>
    /// 极简 WAV(PCM) 写入器。
    /// 先写占位头，关闭时回填真实长度 —— 录制中途断电也不会写出越界长度。
    /// </summary>
    public sealed class WavFileWriter : IDisposable
    {
        private const int HeaderSize = 44;

        private readonly FileStream _stream;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly int _blockAlign;
        private long _dataBytes;
        private bool _closed;

        public WavFileWriter(string path, int sampleRate, int channels, int bitsPerSample)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _blockAlign = channels * bitsPerSample / 8;

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            WritePlaceholderHeader();
        }

        /// <summary>给音频泵写入 PCM 用。</summary>
        public Stream Stream => _stream;

        /// <summary>WAV 文件路径（注意：不要命名为 Path，否则会遮蔽 System.IO.Path）。</summary>
        public string? FilePath => _stream.Name;

        public long DataBytes => _dataBytes;

        /// <summary>PCM 数据由外部写入时调用，用于累计实际字节数。</summary>
        public void AddBytes(int count) => _dataBytes += count;

        private void WritePlaceholderHeader()
        {
            using var bw = new BinaryWriter(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(0);                                   // ChunkSize（回填）
            bw.Write(new[] { 'W', 'A', 'V', 'E' });
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);                                  // Subchunk1Size
            bw.Write((short)1);                            // PCM
            bw.Write((short)_channels);
            bw.Write(_sampleRate);
            bw.Write(_sampleRate * _blockAlign);           // ByteRate
            bw.Write((short)_blockAlign);
            bw.Write((short)_bitsPerSample);
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(0);                                   // Subchunk2Size（回填）
            bw.Flush();
        }

        private void PatchHeader()
        {
            long dataLen = _stream.Length - HeaderSize;
            if (dataLen < 0) dataLen = 0;

            _stream.Seek(4, SeekOrigin.Begin);
            _stream.Write(BitConverter.GetBytes((int)(dataLen + 36)), 0, 4);   // RIFF ChunkSize

            _stream.Seek(40, SeekOrigin.Begin);
            _stream.Write(BitConverter.GetBytes((int)dataLen), 0, 4);          // data Subchunk2Size

            _stream.Flush();
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;

            try
            {
                PatchHeader();
                _stream.Flush();
            }
            catch { }

            try { _stream.Dispose(); } catch { }
        }

        public void Dispose() => Close();
    }
}
