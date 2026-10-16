using System.Threading.Tasks;

namespace GameHub.Interfaces
{
    /// <summary>
    /// 模块接口。每个模块都是内置的，不依赖外部程序。
    /// </summary>
    public interface IModule
    {
        /// <summary>模块名称（同时作为自启动注册表值后缀）。</summary>
        string Name { get; }

        /// <summary>模块描述。</summary>
        string Description { get; }

        /// <summary>模块图标（pack URI）。</summary>
        string IconPath { get; }

        /// <summary>是否已启用该模块的开机自启动。</summary>
        bool IsAutoStartEnabled { get; }

        /// <summary>卡片副标题状态文字，例如"默认输出：耳机"。</summary>
        Task<string> GetStatusTextAsync();

        /// <summary>打开内置功能面板。</summary>
        void OpenPanel();
    }
}
