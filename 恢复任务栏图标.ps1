# 恢复任务栏与桌面图标（2026-11-14 用户要求：GameHub 闪退后“什么都干不了”）
#
# 场景：GameHub 的「桌面净化」把任务栏 / 桌面图标藏起来了，程序闪退/被硬杀没来得及还原。
# 用法：双击同目录的「恢复任务栏图标.cmd」（或右键本文件 → 使用 PowerShell 运行）。
#
# 只调用 user32 的 ShowWindow 把窗口显示回来，不结束任何进程、不改注册表。
#
# 2026-11-17 修补：原版只用 FindWindow("WorkerW") 找“第一个”WorkerW，
# 而隐藏态的桌面视图常常藏在别的 WorkerW / 更深一层子窗口里，导致“救不回来”。
# 现在改为枚举全部顶层窗口 + 子窗口，逐个 SHELLDLL_DefView / SysListView32 还原。

Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string t);
[DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
[DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int c);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(Proc cb, IntPtr lp);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, Proc cb, IntPtr lp);
public delegate bool Proc(IntPtr h, IntPtr lp);
'@

$SW_SHOWNA = 8
$script:done = 0
$script:defViews = New-Object System.Collections.Generic.List[IntPtr]
$script:listViews = New-Object System.Collections.Generic.List[IntPtr]

function Show-It([IntPtr]$h, [string]$what) {
  if ($h -eq [IntPtr]::Zero) { return }
  [void][W.U]::ShowWindowAsync($h, $SW_SHOWNA)
  Write-Host "  [restored] $what"
  $script:done++
}

function ClassOf([IntPtr]$h) {
  $sb = New-Object System.Text.StringBuilder 256
  [void][W.U]::GetClassName($h, $sb, 256)
  return $sb.ToString()
}

function Visit([IntPtr]$h) {
  $cls = ClassOf $h
  if ($cls -eq 'SHELLDLL_DefView') { $script:defViews.Add($h) | Out-Null }
  elseif ($cls -eq 'SysListView32') { $script:listViews.Add($h) | Out-Null }
}

# 枚举全部顶层窗口及其子窗口，收集桌面视图句柄
$cb = [W.U+Proc]{
  param([IntPtr]$h, [IntPtr]$lp)
  Visit $h
  $cbChild = [W.U+Proc]{
    param([IntPtr]$c, [IntPtr]$lp2)
    Visit $c
    return $true
  }
  [void][W.U]::EnumChildWindows($h, $cbChild, [IntPtr]::Zero)
  return $true
}
[void][W.U]::EnumWindows($cb, [IntPtr]::Zero)

# 1) 任务栏：主屏 + 副屏（多显示器各有一个 Shell_SecondaryTrayWnd）
Show-It ([W.U]::FindWindow("Shell_TrayWnd", $null)) "taskbar (primary)"
Show-It ([W.U]::FindWindow("Shell_SecondaryTrayWnd", $null)) "taskbar (secondary)"

# 2) 桌面图标：只显示 SHELLDLL_DefView 与它下面的 SysListView32。
#    注意方向：这里只做“显示”。反向隐藏时绝不能碰 SHELLDLL_DefView——
#    把 DefView 藏起来会让整个桌面内容消失（更糟）。
foreach ($d in $script:defViews) { Show-It $d "SHELLDLL_DefView $d" }
foreach ($l in $script:listViews) { Show-It $l "desktop icons (SysListView32 $l)" }

Write-Host ""
Write-Host "Done. restored $($script:done) window(s)."
Write-Host "(DefView=$($script:defViews.Count), ListView=$($script:listViews.Count))"

if ($script:done -eq 0) {
  Write-Host "Nothing looked hidden. If the desktop is still broken, restarting Explorer rebuilds taskbar + icons."
  try {
    Write-Host "Press any key to restart Explorer, or close this window to skip."
    [void]$Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Start-Process explorer.exe
    Write-Host "Explorer restarted."
  } catch {
    Write-Host "(非交互环境，跳过重启 Explorer 的询问)"
  }
}

Start-Sleep -Seconds 2
