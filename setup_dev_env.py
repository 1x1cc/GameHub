# -*- coding: utf-8 -*-
"""
GameHub 开发环境一键配置脚本
=================================

功能
----
1. 自动定位 VS Code 命令行工具（code / code.cmd）
2. 检查并安装本项目调试所需的全部 VS Code 扩展
3. 用 VS Code 打开 GameHub 项目目录

用法
----
    python setup_dev_env.py            # 安装缺失扩展并打开 VS Code
    python setup_dev_env.py --no-open  # 只安装扩展，不打开 VS Code
    python setup_dev_env.py --list     # 只查看扩展安装状态

说明
----
- 仅使用 Python 标准库，无需 pip 安装任何东西。
- 建议先关闭正在运行的 VS Code，装完扩展后再打开，可避免手动重载。
"""

import argparse
import os
import shutil
import subprocess
import sys

# 脚本所在目录即项目根目录
PROJECT_DIR = os.path.dirname(os.path.abspath(__file__))

# 本项目调试所需的 VS Code 扩展（扩展 ID -> 用途说明）
REQUIRED_EXTENSIONS = [
    ("ms-dotnettools.csdevkit",
     "C# Dev Kit —— F5 调试 / 热重载 / 解决方案管理（必装）"),
    ("ms-dotnettools.csharp",
     "C# —— 语言服务与调试器（C# Dev Kit 的依赖项）"),
    ("ms-dotnettools.vscode-dotnet-runtime",
     ".NET Install Tool —— .NET 运行时检测（C# Dev Kit 的依赖项）"),
    ("ms-python.python",
     "Python —— 运行本配置脚本所需的语言支持"),
]


def safe_print(text=""):
    """兼容 Windows 控制台编码的输出"""
    try:
        print(text)
    except UnicodeEncodeError:
        print(text.encode("gbk", "replace").decode("gbk", "replace"))


def find_code_cli():
    """定位 VS Code 命令行工具，返回可执行文件路径或 None"""
    candidates = []

    for name in ("code", "code.cmd"):
        found = shutil.which(name)
        if found:
            candidates.append(found)

    local_appdata = os.environ.get("LOCALAPPDATA", "")
    program_files = os.environ.get("ProgramFiles", r"C:\Program Files")
    program_files_x86 = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")

    if local_appdata:
        candidates.append(os.path.join(
            local_appdata, "Programs", "Microsoft VS Code", "bin", "code.cmd"))
    candidates.append(os.path.join(
        program_files, "Microsoft VS Code", "bin", "code.cmd"))
    candidates.append(os.path.join(
        program_files_x86, "Microsoft VS Code", "bin", "code.cmd"))

    for c in candidates:
        if c and os.path.isfile(c):
            return c
    return None


def run_code(code_cli, arguments):
    """调用 code 命令，返回 (returncode, 合并后的输出文本)"""
    cmd = [code_cli] + list(arguments)
    try:
        proc = subprocess.run(cmd, capture_output=True, text=True)
    except OSError:
        # .cmd 在某些环境下需要经由系统 shell 启动
        proc = subprocess.run(cmd, capture_output=True, text=True, shell=True)
    output = (proc.stdout or "") + (proc.stderr or "")
    return proc.returncode, output


def list_installed(code_cli):
    """返回已安装扩展 ID 的小写集合；读取失败返回 None"""
    rc, out = run_code(code_cli, ["--list-extensions"])
    if rc != 0:
        return None
    return {line.strip().lower() for line in out.splitlines() if line.strip()}


def main():
    parser = argparse.ArgumentParser(
        description="GameHub 开发环境一键配置（安装 VS Code 扩展并打开项目）")
    parser.add_argument("--no-open", action="store_true",
                        help="只安装扩展，不打开 VS Code")
    parser.add_argument("--list", action="store_true",
                        help="只查看扩展安装状态，不执行安装")
    options = parser.parse_args()

    safe_print("=" * 62)
    safe_print("  GameHub 开发环境一键配置")
    safe_print("=" * 62)
    safe_print("项目目录：{}".format(PROJECT_DIR))

    code_cli = find_code_cli()
    if not code_cli:
        safe_print("")
        safe_print("[错误] 未找到 VS Code 命令行工具 (code.cmd)。")
        safe_print("       请确认已安装 VS Code，或手动把 code 命令加入 PATH。")
        return 1
    safe_print("VS Code ：{}".format(code_cli))
    safe_print("")

    installed = list_installed(code_cli)
    if installed is None:
        safe_print("[警告] 无法读取已安装扩展列表，将尝试直接安装。")
        installed = set()

    safe_print("扩展检查：")
    missing = []
    for ext_id, desc in REQUIRED_EXTENSIONS:
        if ext_id.lower() in installed:
            safe_print("  [已安装] {}".format(ext_id))
        else:
            safe_print("  [缺失  ] {}".format(ext_id))
            missing.append(ext_id)
        safe_print("           {}".format(desc))
    safe_print("")

    if options.list:
        safe_print("（--list 模式：仅查看状态，不执行安装）")
        return 0

    if not missing:
        safe_print("所有所需扩展均已安装，无需操作。")
    else:
        safe_print("开始安装 {} 个缺失扩展……".format(len(missing)))
        failed = []
        for ext_id in missing:
            safe_print("  -> 正在安装 {} ...".format(ext_id))
            rc, out = run_code(code_cli, ["--install-extension", ext_id, "--force"])
            if rc == 0:
                safe_print("     [成功] {}".format(ext_id))
            else:
                safe_print("     [失败] {}".format(ext_id))
                failed.append(ext_id)
                for line in out.splitlines():
                    if line.strip():
                        safe_print("       {}".format(line.strip()))
        safe_print("")
        if failed:
            safe_print("以下扩展安装失败，可稍后重试：")
            for ext_id in failed:
                safe_print("  - {}".format(ext_id))
            safe_print("")

    if options.no_open:
        safe_print("（--no-open 模式：跳过打开 VS Code）")
        return 0

    safe_print("正在用 VS Code 打开项目：{}".format(PROJECT_DIR))
    run_code(code_cli, [PROJECT_DIR])
    safe_print("")
    safe_print("完成。接下来在 VS Code 中：")
    safe_print("  1) 打开 GameHub.sln")
    safe_print("  2) 按 F5 选择 “GameHub (Debug)” 即可实时调试")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        safe_print("")
        safe_print("已取消。")
        sys.exit(130)
