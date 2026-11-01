// 兼容别名：服务器管理模块（移植自 ErieneNeko/PCL-CE-ServerControlPad，Apache-2.0）
// 该分支把 ModMain / ModAnimation / ModBase 重命名为 Launcher / Animations / Common，
// 此处用全局别名让移植过来的文件无需逐个改写成员引用。
global using Launcher = PCL.ModMain;
global using Animations = PCL.ModAnimation;
global using Common = PCL.ModBase;
