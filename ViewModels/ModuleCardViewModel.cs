using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameHub.Interfaces;
using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace GameHub.ViewModels
{
    /// <summary>
    /// 模块卡片视图模型。
    /// 卡片上只有一个按钮：打开内置功能面板。
    /// 点击卡片其它区域同样打开内置功能面板。
    /// </summary>
    public partial class ModuleCardViewModel : ObservableObject
    {
        private readonly IModule _module;

        /// <summary>支持总开关的模块（为 null 时卡片上不显示圆形滑块）。</summary>
        private readonly IModuleSwitch? _switch;

        /// <summary>正在从模块同步状态：此时不要再把值写回模块，否则会死循环。</summary>
        private bool _syncingModuleEnabled;

        private bool _moduleEnabled;

        [ObservableProperty]
        private string _moduleName = string.Empty;

        [ObservableProperty]
        private string _description = string.Empty;

        [ObservableProperty]
        private string _iconSource = string.Empty;

        [ObservableProperty]
        private string _statusText = "正在检测…";

        [ObservableProperty]
        private bool _isAutoStartEnabled;

        /// <summary>是否在卡片上显示圆形总开关。</summary>
        [ObservableProperty]
        private bool _supportsSwitch;

        /// <summary>圆形滑块的悬停提示。</summary>
        [ObservableProperty]
        private string _switchToolTip = string.Empty;

        /// <summary>
        /// 卡片上是否显示「打开」按钮。
        /// GlassBoard 不需要：它的设置全在 Dock 的 ⚙ 里，
        /// 卡片上再放一个「打开」只会多一条把主界面顶出来的路径。
        /// </summary>
        [ObservableProperty]
        private bool _showOpenButton = true;

        /// <summary>
        /// 模块总开关：卡片上的圆形滑块双向绑定到这里。
        /// 打开 / 关闭整个模块（例如 GlassBoard 的桌面美化）。
        /// </summary>
        public bool IsModuleEnabled
        {
            get => _moduleEnabled;
            set
            {
                if (_moduleEnabled == value)
                    return;

                _moduleEnabled = value;
                OnPropertyChanged();
                UpdateSwitchToolTip();

                // 从模块同步过来时不写回，避免来回触发
                if (_syncingModuleEnabled)
                    return;

                try
                {
                    _switch?.SetEnabled(value);
                }
                catch
                {
                    StatusText = "模块切换失败";
                }

                // 开关会立刻改变桌面状态，状态文字要跟着刷新
                _ = RefreshStatusAsync();
            }
        }

        public ICommand OpenPanelCommand { get; }

        public ModuleCardViewModel(IModule module)
        {
            _module = module;
            _switch = module as IModuleSwitch;
            SupportsSwitch = _switch != null;

            // GlassBoard 的入口统一收在 Dock 的 ⚙ 里，卡片上不放「打开」
            ShowOpenButton = true;   // 每张卡片都保留「打开」；GlassBoard 的「打开」直接进 Dock 设置界面

            ModuleName = module.Name;
            Description = module.Description;
            IconSource = module.IconPath;
            IsAutoStartEnabled = module.IsAutoStartEnabled;

            if (_switch != null)
            {
                _syncingModuleEnabled = true;
                IsModuleEnabled = _switch.IsEnabled;
                _syncingModuleEnabled = false;
            }

            UpdateSwitchToolTip();
            OpenPanelCommand = new RelayCommand(OpenPanel);
        }

        /// <summary>滑块提示文案：说清当前状态和怎么操作。</summary>
        private void UpdateSwitchToolTip()
        {
            if (!SupportsSwitch)
            {
                SwitchToolTip = string.Empty;
                return;
            }

            SwitchToolTip = IsModuleEnabled
                ? "模块已开启：点击或左右拖动可关闭；关闭后时钟 / Dock / 面板全部停止，桌面还原"
                : "模块已关闭：点击或左右拖动可开启；开启后按上次记忆恢复时钟 / Dock / 面板";
        }

        /// <summary>异步拉取模块状态（默认设备名 / 蓝牙连接状态）。</summary>
        public async Task RefreshStatusAsync()
        {
            IsAutoStartEnabled = _module.IsAutoStartEnabled;

            // 面板里改过状态（例如点了“一键恢复”）时，把卡片滑块同步回来
            if (_switch != null)
            {
                _syncingModuleEnabled = true;
                IsModuleEnabled = _switch.IsEnabled;
                _syncingModuleEnabled = false;
            }

            try
            {
                StatusText = await _module.GetStatusTextAsync();
            }
            catch
            {
                StatusText = "状态读取失败";
            }
        }

        private void OpenPanel() => _module.OpenPanel();
    }
}
