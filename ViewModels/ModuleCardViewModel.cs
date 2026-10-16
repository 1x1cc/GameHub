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

        public ICommand OpenPanelCommand { get; }

        public ModuleCardViewModel(IModule module)
        {
            _module = module;

            ModuleName = module.Name;
            Description = module.Description;
            IconSource = module.IconPath;
            IsAutoStartEnabled = module.IsAutoStartEnabled;

            OpenPanelCommand = new RelayCommand(OpenPanel);
        }

        /// <summary>异步拉取模块状态（默认设备名 / 蓝牙连接状态）。</summary>
        public async Task RefreshStatusAsync()
        {
            IsAutoStartEnabled = _module.IsAutoStartEnabled;

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
