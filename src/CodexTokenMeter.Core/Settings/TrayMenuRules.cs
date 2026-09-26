namespace CodexTokenMeter.Core.Settings;

/// <summary>
/// 托盘菜单的交互规则。
/// </summary>
/// <remarks>
/// <para>
/// 放在 Core 而不是 UI 项目，原因有二：
/// 一是可以让判定逻辑被单元测试覆盖，而菜单本身无法在测试里点击；
/// 二是 UI 项目同时引用 WPF 与 WinForms，把纯逻辑混在里面容易
/// 被误当作“与某个框架绑定的代码”。
/// </para>
/// <para>
/// 本类型不依赖任何 UI 框架——调用方把枚举值映射为自己的类型即可。
/// </para>
/// </remarks>
public static class TrayMenuRules
{
    /// <summary>
    /// 判断「胶囊显示指标」子菜单在点击一项后是否应保持打开。
    /// </summary>
    /// <param name="closeReasonIsItemClicked">
    /// 关闭原因是否为「点击了菜单项」。false 表示点到别处、按 Esc、
    /// 窗口失活等。
    /// </param>
    /// <param name="allowClose">
    /// 本次点击是否被显式允许关闭菜单（例如点了「恢复默认」）。
    /// </param>
    /// <remarks>
    /// WinForms 默认在点击菜单项后关闭整条菜单链，于是每改一项都要
    /// 重新右键展开。对一个 18 项、常需组合调整的列表而言体验很差，
    /// 因此勾选项点击后保持打开。
    ///
    /// 但不能无条件保持：点到别处或按 Esc 时必须正常关闭，
    /// 否则菜单会“粘”在屏幕上挡住内容。
    /// </remarks>
    public static bool ShouldKeepMenuOpen(
        bool closeReasonIsItemClicked,
        bool allowClose) =>
        closeReasonIsItemClicked && !allowClose;
}
