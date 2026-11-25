# Damage Tracker

[English](README.md)

Damage Tracker 是一个《Slay the Spire 2》伤害统计模组（基于 [BAIGUANGMEI/STS2-DamageTracker](https://github.com/BAIGUANGMEI/STS2-DamageTracker) 的社区修改版），会在战斗界面显示一个可拖动、可缩放、可防误触锁定的伤害统计面板，用于展示当前 Run 中每位玩家的输出情况。

当前界面支持：

- 完整展开模式、紧凑模式与侧边隐藏模式
- 点击列头自由排序（占比 / 总计 / 战斗 / 本轮 / 上次 / 最大）
- 伤害来源拆解（直接伤害 / 中毒 / 灾厄 / 召唤物与充能球 / 其他）与悬停展示每位玩家伤害最高的 Top 3 卡牌
- 历史战斗快照切换（`本局` + 历次已完成战斗及遭遇名）
- 一键复制战报到剪贴板（附带输出担当 / 补刀王称号）
- 锁定鼠标穿透（`F9`）、全局快捷键显隐（`F8`）、战斗外自动淡化、背景不透明度切换
- 右下角拖动自由调整面板宽高与 `Ctrl + 滚轮` 缩放字号比例（`0.7x` – `2.5x`）
- 自动记忆布局设置与战前存档检查点（防止中途读档重复统计）
- 修复角色贴图卸载时导致队友行丢失的问题，渲染更稳健

当前版本：`0.5.0`

## 功能

- 统计每位玩家当前 Run 的总伤害、本场伤害、本轮伤害、上次命中、最高单击、击杀数与溢出伤害
- 精细归类伤害来源（直接伤害 / 中毒 / 灾厄 / 召唤物与充能球 / 其他）——支持回合中毒与卡牌触发中毒（如 `Outbreak`）、亡灵召唤物（`Osty`）及充能球被动/激发伤害，同时自动过滤玩家自伤（如献祭/放血）
- 鼠标悬停在玩家行上显示完整精确数据、击杀、溢出伤害、各来源占比及伤害最高的前三张牌
- 支持通过 `‹` / `›` 切换查看整局累计数据或任意已完成战斗的历史快照
- 展开模式下点击任意数据列头即可切换排序字段与升降序
- 标题栏一键复制整齐的纯文本战报到剪贴板（带「输出担当」「补刀王」称号）
- 高亮当前正在操作的玩家、显示角色主题色与占比进度条，打出个人新最高单击时触发金色边框闪烁提示
- 支持 `F9` 锁定鼠标穿透模式（含内部滚动条与侧边标签）、`F8` 快捷键开关显示、战斗外自动淡化以及背景不透明度三档切换（`70%` / `45%` / `88%`）
- 支持鼠标拖动面板、右下角拖动缩放（双击恢复默认）与 `Ctrl + 滚轮` 缩放（`0.7x` – `2.5x`）
- 自动保存全部布局设置（`scale`、`width`、`compact_width`、`height`、`x`、`y`、`expanded`、`locked`、`auto_dim`、`background_opacity`）到 `user://damage_tracker_ui.json`
- 内置完整中英文回退词典，同时支持加载 `assets/localization/{lang}/damage_tracker.json` 覆盖（无需重新导出 `DamageTracker.pck` 也能完整显示全部新文案）

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
- **移动与缩放**：左键拖动面板移动；拖动右下角手柄调整宽高（双击手柄恢复默认）；悬停面板按住 `Ctrl + 滚轮` 缩放整体字号比例（`0.7x` – `2.5x`）
- **快捷键**：按 `F8` 切换显示/隐藏整个面板；按 `F9` 切换锁定/解锁鼠标穿透模式
- **顶部快捷按钮**：
  - `Cp`：一键复制当前视图（整局或选定历史战斗）的战报到剪贴板
  - `Op`：循环切换背景不透明度（`70%` → `45%` → `88%`）
  - `Dm`：开启/关闭战斗外自动淡化（战斗外降至 `35%` 不透明度）
  - `Lk`：切换锁定鼠标穿透模式（锁定后按 `F9` 解锁）
  - `▼` / `▶`：在完整展开模式与紧凑模式间切换
  - `»` / `«`：收起到左侧或右侧屏幕边缘（点击侧边小标签恢复）
- **视图切换栏（`‹ [范围] ›`）**：在「本局」与历次已完成战斗之间切换回看
- **排序与明细**：点击列头（`%` / `总计` / `战斗` / `本轮` / `上次` / `最大`）切换排序；鼠标悬停在玩家行上查看击杀、溢出伤害、伤害来源占比及 Top 3 输出卡牌

## 伤害统计规则

- 直接伤害基于敌方实际结算的 `DamageResult.UnblockedDamage`（自动过滤玩家自伤）
- 中毒伤害通过 `PoisonPower.Trigger()` 捕获，同时覆盖回合开始结算与卡牌触发中毒（如 `Outbreak`）
- 灾厄伤害只在真实 `DoomPower.DoomKill` / `Hook.AfterDiedToDoom` 流程中按目标死亡前 HP 记账
- 召唤物（`Creature.IsPet` / `PetOwner`）与充能球（`OrbModel` 激发及回合末被动）统一归类为「召唤物 / 充能球」
- 玩家归属优先使用 STS2 v0.113.0 强类型 API（`ICombatState`、`DamageResult`、`CardModel`、`PlayerChoiceContext`），当 `NetId == 0` 时使用对象哈希兜底
- 使用战前已提交检查点机制，战斗中途退出读档重开不会重复累加伤害或场次

## 项目结构

- `src/ModEntry.cs`：模组入口、`ICombatState` Harmony 补丁与中毒/灾厄/充能球异步上下文跟踪
- `src/RunDamageTrackerService.cs`：伤害聚合、来源与单卡明细、历史战斗快照、战前检查点与原子持久化
- `src/BattleReportFormatter.cs`：纯文本战报与 MVP/补刀王称号格式化器
- `src/ReflectionHelpers.cs`：运行时对象、卡牌名、遭遇名与伤害事件解析辅助
- `src/DamageTrackerOverlay.cs`：伤害覆盖层 UI、列头排序、历史切换、锁定穿透与快捷键、透明度/自动淡化、缩放持久化与内置多语言回退
- `assets/localization/{eng,zhs}/damage_tracker.json`：英文与简体中文本地化文件
- `DamageTracker.csproj`：项目配置与游戏引用路径
- `DamageTracker.json` 与 `mod_manifest.json`：模组元数据与清单
- `project.godot`：Godot 项目配置

## 环境要求

- Windows
- .NET 9 / 10 SDK
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

### v0.5.0

- 新增伤害来源拆解（直接伤害 / 中毒 / 灾厄 / 召唤物与充能球 / 其他）与悬停展示每位玩家 Top 3 输出卡牌
- 新增本轮伤害（`本轮`）、击杀数、溢出伤害统计，支持点击列头切换排序（`%` / `总计` / `战斗` / `本轮` / `上次` / `最大`）
- 新增历史战斗快照切换（`本局` + 历次已完成战斗及遭遇名）与一键复制战报（`BattleReportFormatter`，含输出担当与补刀王称号）
- 新增锁定鼠标穿透模式（`F9`，同步穿透内部滚动条与侧边标签）、全局显隐快捷键（`F8`）、战斗外自动淡化、背景不透明度三档切换及破个人最高单击金色边框高光
- 升级 Harmony 补丁为 STS2 v0.113.0 精确 `ICombatState` 签名，改用 `AfterDamageGiven` Prefix 提前捕获来源上下文、改挂 `PoisonPower.Trigger()`（修复 `Outbreak` 漏记）、过滤玩家自伤，并新增战前检查点修复中途读档重开重复统计
- 内置完整中英文回退词典并同步更新本地化 JSON，无需重新导出 `DamageTracker.pck` 即可完整显示新文案

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

### 面板锁定时点不到按钮或无法拖动

按 `F9` 即可解锁鼠标穿透模式。

### 如何重置面板位置、尺寸或缩放比例

双击面板右下角缩放手柄即可恢复默认缩放与宽高；也可以在关闭游戏后编辑或删除游戏用户数据目录下的 `damage_tracker_ui.json`。

## 许可证

本项目采用 MIT License，详见 [LICENSE](LICENSE)。

该许可证适用于仓库中的模组源码和自定义资源，不适用于游戏本体、商业游戏资源或第三方受限内容。
