# Damage Tracker

[English](README.md)

Damage Tracker 是一个《Slay the Spire 2》伤害统计模组（基于 [BAIGUANGMEI/STS2-DamageTracker](https://github.com/BAIGUANGMEI/STS2-DamageTracker) 的社区修改版），会在战斗界面显示一个可拖动、可缩放的伤害统计面板，用于展示当前 Run 中每位玩家的输出情况。

当前界面支持：

- 完整展开模式与紧凑模式
- 侧边隐藏模式（只保留一个小标签）
- 右下角拖动自由调整面板宽高（展开/紧凑模式宽度独立记忆，双击恢复默认）
- `Ctrl + 滚轮` 缩放界面比例与字号（`0.7x` – `2.5x`）
- 自动记忆窗口位置、尺寸、缩放比例与展开状态
- 高度自适应，玩家较多或窗口分辨率变化时自动适配并支持滚动
- 修复角色贴图卸载时导致队友行丢失的问题，渲染更稳健
- 连续多局运行时更稳定的显示行为

当前版本：`0.4.0`

## 功能

- 统计每位玩家当前 Run 的总伤害
- 统计当前战斗伤害、最近一次命中伤害与单次最高伤害
- 高亮当前正在操作的玩家，并显示角色主题色与伤害占比进度条
- 尽可能显示平台玩家名、角色名和角色头像（贴图不可用或已卸载时自动降级为首字母头像）
- 大数值自动缩略显示（`k` / `M`），鼠标悬停在玩家行上可查看完整精确数据
- 支持鼠标拖动面板并自动保存位置
- 支持右下角拖动缩放窗口，以及 `Ctrl + 滚轮` 缩放字号与整体比例（双击右下角恢复默认）
- 自动保存界面布局配置（`scale`、`width`、`compact_width`、`height`、`x`、`y`、`expanded`）到 `user://damage_tracker_ui.json`
- 支持展开、紧凑、侧边隐藏三种界面状态
- 在战斗开始和结束时自动刷新状态
- 支持连续开启多轮新 Run，面板显示更稳定

## 安装方式

请在 [Releases](https://github.com/dsadsada429/STS2-DamageTracker/releases) 页面下载最新版本，并解压到游戏根目录下的 `mods` 文件夹。

包含文件：

- `DamageTracker.dll`
- `DamageTracker.pck`
- `DamageTracker.json`

推荐目录结构：

```text
Slay the Spire 2/
  mods/
    DamageTracker/
      DamageTracker.dll
      DamageTracker.pck
      DamageTracker.json
```

## 使用说明

- 进入战斗后会自动创建伤害统计面板
- 按住左键可以拖动面板位置（松开后自动保存）
- 拖动面板右下角手柄可以自由调整窗口宽度和高度
- 双击面板右下角手柄可以将缩放比例、宽度和高度恢复默认
- 鼠标悬停在面板上时按住 `Ctrl` 并滚动滚轮，可以缩放界面和字号（`0.7x` – `2.5x`）
- 鼠标悬停在任意玩家行上可查看完整未缩略的伤害统计提示
- 点击右上角折叠按钮（`▼` / `▶`）可以在完整模式和紧凑模式之间切换
- 点击侧边隐藏按钮（`»` / `«`）可以把面板收进左侧或右侧屏幕边缘，点击侧边小标签可重新展开
- 当玩家较多或手动调小面板高度时，列表会自动启用纵向滚动

## 伤害统计规则

- 普通直接伤害基于游戏真实结算后的 `DamageResult.UnblockedDamage`
- Poison 不再根据层数预估，而是跟随实际伤害结算记录
- Doom 只在真实 Doom 斩杀流程中统计，并按目标死亡前的实际 HP 记账
- 玩家归属优先使用 STS2 强类型 API（当 `NetId == 0` 时自动使用对象哈希兜底，避免玩家键冲突），反射仅作为兜底

## 技术实现

- 使用 Harmony 对 STS2 Hook 和部分 Power 方法进行补丁
- 使用 Godot `CanvasLayer` 渲染覆盖层 UI
- 通过 `RunState.Rng.StringSeed` 识别 Run 生命周期
- Run 伤害进度保存在 `user://damage_tracker_state.json`，界面布局配置保存在 `user://damage_tracker_ui.json`
- 在开局和开战时预注册玩家，避免面板显示依赖“谁先行动”
- 绑定 `Texture2D` 前先校验资源生命周期并清理失效缓存，同时对每一行渲染进行异常隔离与降级兜底，防止贴图卸载导致队友行消失

## 项目结构

- `src/ModEntry.cs`：模组入口和 Harmony 补丁注册
- `src/RunDamageTrackerService.cs`：伤害统计与状态管理
- `src/ReflectionHelpers.cs`：运行时对象解析和玩家归属辅助
- `src/DamageTrackerOverlay.cs`：伤害显示界面、窗口缩放、布局持久化与容错渲染
- `DamageTracker.csproj`：项目配置与游戏引用路径
- `DamageTracker.json`：STS2 模组元数据
- `mod_manifest.json`：模组清单
- `project.godot`：Godot 项目配置

## 环境要求

- Windows
- .NET 10 SDK
- Godot .NET SDK 4.5.1
- 本地已安装《Slay the Spire 2》

构建前请先修改 `DamageTracker.csproj` 中的游戏目录：

```xml
<Sts2Dir>C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2</Sts2Dir>
```

项目会从以下目录引用游戏程序集：

```text
$(Sts2Dir)\data_sts2_windows_x86_64
```

依赖的程序集包括：

- `sts2.dll`
- `0Harmony.dll`
- `Steamworks.NET.dll`

## 构建

在仓库根目录执行：

```powershell
dotnet build
```

构建成功后会生成：

```text
.godot/mono/temp/bin/Debug/DamageTracker.dll
```

项目还配置了自动复制，会尝试把构建产物复制到：

```text
Slay the Spire 2\mods\DamageTracker\
```

## 更新说明

### v0.4.0

- 新增右下角拖动手柄，支持自由调整面板宽高（展开/紧凑模式宽度独立记录，双击手柄恢复默认）
- 新增 `Ctrl + 滚轮` 缩放界面比例与字号（`0.7x` – `2.5x`）
- 新增界面布局持久化（`user://damage_tracker_ui.json`），自动记住缩放比例、宽高、位置和展开状态
- 修复对局中角色贴图卸载导致队友行丢失的问题：校验 `Texture2D` 有效性、清理失效图标缓存，并对单行渲染异常提供降级行兜底
- 优化长名称截断省略（ellipsis）、大数值紧凑格式（`k` / `M`）、行悬停完整数据提示与窗口分辨率变化自适应
- 修复离线/本地模式下 `Player.NetId == 0` 时的玩家键兜底逻辑

### v0.3.0

- 修复连续多局后面板可能缺失玩家行的问题
- 优化 Run 与玩家归属解析，优先使用 STS2 强类型 API
- 修复角色颜色高亮异常
- 重做 Poison 与 Doom 的统计逻辑，改为基于真实结算
- 优化界面稳定性，新增紧凑模式、侧边隐藏和自适应高度

## 常见问题

### 构建失败，提示找不到游戏 DLL

请检查 `DamageTracker.csproj` 中的 `Sts2Dir` 是否指向你的真实游戏目录。

### 构建失败，提示输出 DLL 被占用

如果游戏正在运行，`DamageTracker.dll` 可能被 `SlayTheSpire2.exe` 锁定。关闭游戏后重新构建即可。

### 新开一局后面板不显示或显示不完整

请使用 `0.3.0` 或更高版本。当前版本已经改成在开局和开战时预注册玩家，并优化了连续 Run 的处理逻辑。

### 玩家名或头像没有正确显示

当前会优先使用平台和游戏运行时数据；如果角色贴图不可用或已卸载，面板会安全降级为彩色首字母头像。如果游戏更新导致字段变化，可能需要同步调整 `ReflectionHelpers.cs`。

### 如何重置面板位置、尺寸或缩放比例

双击面板右下角缩放手柄即可恢复默认缩放与宽高；也可以在关闭游戏后编辑或删除游戏用户数据目录下的 `damage_tracker_ui.json`。

## 开发说明

这个项目主要用于源码学习和模组开发练习。建议保留源码、配置文件和清单文件，不要提交构建缓存或本地临时文件。

仓库当前已忽略常见生成文件，例如：

- `.godot/`
- `bin/`
- `obj/`
- `.vs/`
- `*.dll`
- `*.pck`

## 许可证

本项目采用 MIT License，详见 [LICENSE](LICENSE)。

该许可证适用于仓库中的模组源码和自定义资源，不适用于游戏本体、商业游戏资源或第三方受限内容。
