// =====================================================================
//  程序集与文件版本资源
//  作用有两个：
//    1) 让资源管理器/属性页有正规的厂商、产品、版本信息；
//    2) 无版本资源 + 无签名的裸 exe 是 SmartScreen / Defender 云查杀
//       最典型的「无法识别的应用」画像，补齐元数据能显著降低误报。
// =====================================================================
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("GWQuota 上游额度悬浮窗")]
[assembly: AssemblyDescription("只读展示上游 API 余额与包月额度用量的桌面悬浮窗")]
[assembly: AssemblyProduct("GWQuota")]
[assembly: AssemblyCompany("gw-quota contributors")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: AssemblyConfiguration("Release")]
[assembly: ComVisible(false)]
[assembly: Guid("8f2f3c1a-5d64-4c1e-9a0b-2c7d6e5f4a10")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
