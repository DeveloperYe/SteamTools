// Android 工程专用全局 using，并消解跨库类型歧义。

global using BD.WTTS;
global using BD.WTTS.Enums;
global using BD.WTTS.Models;
global using BD.WTTS.Services;
global using Microsoft.Extensions.DependencyInjection;
global using NLog;

// 消歧：Polly 的 Context/Activity 与 Android 的同名类型冲突
global using Activity = Android.App.Activity;
global using Context = Android.Content.Context;