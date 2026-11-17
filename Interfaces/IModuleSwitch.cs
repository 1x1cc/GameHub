namespace GameHub.Interfaces
{
    /// <summary>
    /// 支持“卡片总开关”的模块。
    ///
    /// 实现这个接口的模块，卡片右上角会出现一个圆形滑块，
    /// 用来一次开启 / 关闭整个模块（例如 GlassBoard 的桌面美化）。
    /// </summary>
    public interface IModuleSwitch
    {
        /// <summary>模块当前是否开启。</summary>
        bool IsEnabled { get; }

        /// <summary>
        /// 开启或关闭模块。
        ///
        /// 约定：关闭时必须撤销模块已经造成的影响，不能留下
        /// “关掉了却还原不回去”的状态。
        /// </summary>
        void SetEnabled(bool enabled);
    }
}
