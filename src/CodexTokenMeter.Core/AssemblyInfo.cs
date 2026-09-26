using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// DLL 搜索路径限制。
//
// 不限定搜索路径时，Windows 会先在**当前工作目录**查找 DLL，
// 然后才是系统目录。用户若从下载目录双击运行，
// 同名的恶意 user32.dll / dwmapi.dll 会被优先加载（DLL 劫持），
// 攻击者由此获得与浮层相同的权限。
//
// System32 限定后，只从系统目录加载。本程序不依赖任何第三方原生 DLL，
// 因此这个限制不会影响功能。
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

// 仅使用 Windows 平台的 API。
[assembly: SupportedOSPlatform("windows")]
