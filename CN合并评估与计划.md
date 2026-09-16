# CN → Violet 合并评估与计划

评估日期：2026-09-16。以下评估保留实施前基线；用户确认后的实际变更与验证结果见文末“实施记录”。

**结论**

可以合并，但不能直接自动合完就发版。实际预演得到 16 个冲突文件：12 个内容冲突、4 个修改/删除冲突。工作量判断为中等，主要集中在登录流程迁移和 Violet 更新源保留；无需重写整个启动器。机器码数据结构两边完全一致，可以继续复用现有预设关联字段，不需要增加第二套机器码存储。

**评估基线与已完成检查**

| 项目 | 本次实际状态 |
| --- | --- |
| 当前分支 | `Violet`，`ae63a688c9ca440393f2cd9d7183c56f63f5182d`，2.3.9 |
| 合并来源 | `origin/CN`，`d902b1791ea65c203f5b4704473bd6cbd58dafd9`，2.4.9 |
| 远端 | `https://github.com/MilkVio/FFXIVQuickLauncher.git` |
| 共同祖先 | `58bdd1b0ff6cdabdcb5d6c77b008bcfdd727ced9`，上次已合入的 CN 2.3.9 |
| 独有提交数 | CN 52 个；Violet 13 个，包含历史合并提交 |
| CN 相对共同祖先的净变更 | 98 个文件，新增 5403 行、删除 2482 行，含大量重构和格式调整 |
| 本地 `CN` 分支 | 停在旧提交 `6708587b`；实施应使用本次确认的 `origin/CN`，不要误合本地 `CN` |
| 预演方式 | `git merge-tree --write-tree --messages Violet origin/CN` |
| 预演产物 | 临时树 `288960235e5ae8113a4460e8e3ba8546c3ef1b91`；含冲突标记，不是可发布源码 |
| 工作区 | 检查前干净；fetch 只更新远端跟踪引用，预演只生成 Git 对象；本次仅新增本文 |

检查覆盖两边提交差异、真实三方合并冲突、机器码模型/设置/登录调用链、更新和发布差异。尚未编译合并代码，也未登录真实账号，因此“可合并”是源码评估结论，不代表运行验收已经通过。

**16 个实际冲突及处理方向**

路径相对于仓库根目录。表内重命名后的路径来自 Git 预演结果。

| 文件 | 冲突类型 | 建议处理 |
| --- | --- | --- |
| `src/XIVLauncher.Common/Constant/Links.cs` | 内容 | 保留 CN 最新资源常量；仓库和启动器更新仍指向 MilkVio；补齐 Violet `GitHubSource` 需要的常量 |
| `src/XIVLauncher.Login/Workflow/LoginWorkflowRequest.cs` | 内容 | 使用新 `ILoginWorkflowUI`，接入扫码开关，保留 Debug 字段 |
| `src/XIVLauncher.Login/Workflow/LoginWorkflowService.cs` | 内容 | 保留 Violet 扫码后绑定预设、凭据保存、Debug；适配 CN 新接口及登录身份处理 |
| `src/XIVLauncher.Login/Workflow/NewAccountDeviceProfileCoordinator.cs` | 内容 | 合并成一条扫码准备流程，统一开关与选择结果，避免前后重复询问或更换快照 |
| `src/XIVLauncher/Resources/CHANGELOG.txt` | 内容 | 整理 CN 2.4.9 更新及 Violet 保留功能，维持 Violet 文案 |
| `src/XIVLauncher/Settings/LauncherSettingsV3.cs` | 内容 | 合入扫码独立开关并迁移旧值；保留 Debug、全局禁止轮换 |
| `src/XIVLauncher/Update/GitHubSource.cs` | 修改/删除 | 保留 Violet 实现，CN 删除它不适用于 Violet 的 GitHub 发版方式 |
| `src/XIVLauncher/Update/UpdateOrchestrator.cs` | 内容 | 保持读取 Violet GitHub Releases；按需接入 CN 网络环境能力，避免切回 Soil 分发源 |
| `src/XIVLauncher/Windows/ChangelogWindow.xaml.cs` | 内容 | 使用 CN 窗口更新，保留 Violet 品牌 |
| `src/XIVLauncher/Windows/SettingsWindow.xaml` | 内容 | 合入 CN 新布局，统一扫码设置入口，保留 Violet 两个附加开关 |
| `src/XIVLauncher/Windows/ViewModel/Main/Flows/GameLaunchFlow.cs` | 内容 | 采用 CN 新流程结构，迁移 Violet 品牌文案 |
| `src/XIVLauncher/Windows/ViewModel/Main/Flows/LoginFlow.cs` | 内容 | 采用 CN 新流程结构，创建包含扫码设置和 Debug 的完整请求 |
| `src/XIVLauncher/Windows/ViewModel/Main/Providers/MainWindowDialogProvider.cs` | 修改/删除 | 将 Violet 机器码选择窗口调用迁入新 UI service 后删除旧类 |
| `src/XIVLauncher/Windows/ViewModel/Main/Providers/MainWindowLoginInteraction.cs` | 修改/删除 | 将机器码 Debug 和选择交互迁入 `MainWindowLoginUIService`，然后删除旧类 |
| `src/XIVLauncher/Windows/ViewModel/Main/Services/GameLaunchService.cs` | 修改/删除 | 跟随 CN 拆分到新 Flow/Service；此处 Violet 差异主要是品牌文案，无需保留旧服务架构 |
| `src/XIVLauncher/Windows/ViewModel/SettingsWindowViewModel.cs` | 内容 | 完整接通扫码开关、旧值迁移后的显示与保存、Debug 和禁用轮换设置 |

不能采用整批“全部选 ours/theirs”。例如全取 CN 会丢失 Violet 的更新器和扫码扩展；全取 Violet 会留下已经被 CN 主窗口重构淘汰的服务结构。

**自动合并成功仍需检查的地方**

- `ILoginWorkflowInteraction` 自动重命名为 `ILoginWorkflowUI` 后，预演接口同时包含两边的扫码方法和 Violet 的 Debug 方法；CN 新增的 `MainWindowLoginUIService` 没有实现 Violet 扩展。它不在冲突文件列表中，但必须适配，否则会出现接口实现错误。
- `AccountManager.cs` 能自动合并；需要同时保住 CN 的数据库损坏隔离修复和 Violet 的禁止轮换、读取现有独立预设、失败后清理预设逻辑。
- `SavedAccountLoginResolver` 新增扫码前清空用户名。要保证最终绑定的是扫码返回的实际账号，不把当前选中的账号当作扫码身份。
- 新增的 `GameInjectionFlow`、`DalamudLaunchService`、`MainWindowLoginUIService` 仍带 Soil 标题，Git 不会自动替换为 Violet。
- CN 新增 `cnb-upload.yml` 和 `cnb-backfill.sh`，脚本目标硬编码为 `atmoomen/xivlauncher-distribute`。建议从 Violet 合并结果中排除这两个专用分发文件，继续使用现有 `ci-github-release.ps1` 和 Violet workflow。
- 保留 Violet 的图标、主题、产品名称、本地 Debug 跳过 native shim 构建的配置；合入 CN 的版本号、窗口/新闻/性能改进。
- 现有 `UpdateOrchestratorTests` 使用 Soil `SimpleWebSource` 的真实 URL，不能据此证明 Violet `GitHubSource` 更新链路正确。

**机器码字段复用方案**

对比 `XIVAccount.cs`、`DeviceProfiles/` 和 `ResolvedDeviceProfile.cs`，当前两分支之间没有差异。CN 新增的是扫码前选择设备信息的入口和开关，并非另一套机器码协议或数据库结构。

| 用途 | 继续使用的字段/对象 | 合并原则 |
| --- | --- | --- |
| 账号关联某个预设 | `XIVAccount.DeviceProfilePresetId` | 复用此字段，不增加 CN/Violet 各自的机器码 ID |
| 是否采用账号独立预设 | `DeviceProfileDynamicEnabled` | 保持现有语义；不能拿它当“是否弹出扫码选择窗口”的开关 |
| 实际发送的设备信息 | `DeviceProfileSnapshot`：`DeviceId`、`MacAddress`、`HostName` | 必须整体复用同一份快照；`MacHash`、`CasCid` 由 MAC 派生，不能只拼接复用一个 `DeviceId` |
| 预设落盘 | `deviceProfilePresets.json` | 共用现有预设仓库；`accounts.db` 继续存关联信息 |
| 旧版账号内机器码列 | `DeviceProfileDeviceId/MacAddress/HostName` | 保留现有迁移兼容，不重新启用为第二套存储 |
| 扫码前是否选择 | `RequireDeviceProfileSetupForQRCodeLogin` | 建议采用 CN 字段，作为统一扫码流程的唯一控制字段 |
| 非扫码新账号是否先设置 | `RequireDeviceProfileSetupForNewLogin` | 保留此独立用途，避免新账号设置与扫码设置永久耦合 |
| Violet 附加能力 | `DeviceProfileDebugEnabled`、`DisableAllDeviceProfileRotation` | 保留各自用途，不与扫码开关合并 |

因此，如果“复用一个字段”指账号机器码关联，答案是直接复用 `DeviceProfilePresetId`；如果指扫码功能开关，建议统一使用 CN 的 `RequireDeviceProfileSetupForQRCodeLogin`。不需要再增加一个 Violet 专用扫码开关。

旧配置迁移建议：读取配置 JSON 时判断扫码字段是否存在。不存在时，将旧 `RequireDeviceProfileSetupForNewLogin` 的值复制给扫码字段一次并保存，保持原 Violet 的扫码行为；已存在时保留显式值，包括 `false`。不要用布尔 OR 长期兜底，否则用户无法单独关闭扫码选择。全新配置两个开关继续默认 `false`。该迁移针对现有 Violet 升级；如果还要支持导入没有分支标记的更早 Soil 配置，其历史来源无法仅凭字段缺失区分，需要另行定义导入策略。

扫码交互建议保留 Violet 的“新建独立机器码／使用共享机器码／沿用已有账号机器码”，并让需要编辑设备信息时使用 CN 更新后的设备设置窗口。全部选择进入同一条准备、请求、保存路径，不并行保留两套扫码弹窗和协调器。若接入 CN 的临时编辑路径，也应将变更暂存到登录成功后，避免取消扫码留下新预设。

需要保住的行为：

1. 扫码前选定完整快照，二维码请求、后续快速登录和最终保存使用同一份设备信息。
2. “沿用已有账号”读取已存预设时不触发轮换；来源账号只是机器码来源，目标账号由扫码结果确定。
3. 新建独立预设在扫码成功、获得所需凭据后才正式绑定；取消、超时或请求失败不新增错误账号/孤立预设。
4. 重扫已有账号时核查并保留备注、排序、已有凭据和轮换设置；只更新用户本次明确选择的设备关联及新返回的凭据。
5. 扫码开关关闭时不弹出设备选择；明确采用解析器返回的默认设备行为，并测试“当前选中 A、实际扫码 B”。
6. 两个提示开关同时开启时扫码只询问一次；不再在扫码后重复询问并换用另一台设备信息。
7. 全局禁用轮换优先于每账号轮换设置，但不覆盖账号原始开关；Debug 显示实际请求使用的完整快照。

**建议实施顺序**

1. 从已确认的 Violet HEAD 建立 `codex/merge-cn-2.4.9` 集成分支，固定合入 `d902b179`。采用正常 merge 保留历史，方便后续 CN 同步；不逐个 cherry-pick 这 52 个提交。实施前若远端移动，重新确认差异范围。
2. 接入 CN 主窗口 Flow/Service、接口重命名、新闻/性能/窗口改进和数据库修复；将 Violet 的 UI 扩展搬到新结构。清理已淘汰旧类和引用。
3. 统一机器码扫码流程与字段；加入旧配置的一次性迁移，保留 Violet 预设选择、Debug 和全局禁止轮换。重点审查凭据、临时预设与实际扫码账号的关联。
4. 恢复并核对 Violet 的 GitHub 更新源、发布 workflow、品牌和本地构建配置，排除上游专用 CNB 发布脚本；整理 2.4.9 合并日志。
5. 按下面的回归矩阵验证，修复后形成可评审 diff。通过后再考虑合回 Violet 和发版，计划阶段不创建 tag 或触发发布。

**实施后的验证要求**

| 检查 | 通过标准 |
| --- | --- |
| 编译与引用 | `dotnet build src/XIVLauncher.slnx -c ReleaseNoUpdate` 通过，旧接口/服务引用及冲突标记清零；正式发布构建仍包含所需 native shims |
| 旧配置升级 | 旧开关为 true/false 分别正确迁移；扫码字段显式 false 不被覆盖；二次启动不重复迁移；配置备份恢复路径同样处理 |
| 数据兼容 | 用临时副本加载旧 `accounts.db` 与 `deviceProfilePresets.json`；预设 ID、设备三元组、每账号轮换配置保持兼容 |
| 扫码三种选择 | 新建、共享、沿用已有账号分别验证新账号和已有账号，核对最终保存的账号身份与发送快照 |
| 失败路径 | 选择取消、二维码过期、网络失败、凭据保存失败，不产生错误绑定；新建预设按既定规则清理 |
| 登录衔接 | 扫码成功后快速登录、Session 刷新、登录回退到扫码时设备身份一致；已有账号备注/排序/凭据不意外丢失 |
| 设置组合 | 两个设备提示开关组合、每账号轮换与全局禁用组合；Debug 对扫码和快速登录显示实际值 |
| 其他流程 | 密码/验证码、WeGame、超域旅行重新拉起、手动注入、账号切换做针对性回归 |
| Violet 更新 | 使用可控 release feed 验证 MilkVio 仓库、正式/预发布过滤、全量/增量包选择；不会请求 Soil 启动器安装包 |
| UI | 新设置窗口、扫码选择、设备编辑、日志窗口可正常打开，无绑定错误，Violet 主题和品牌保留 |

现有测试主要覆盖补丁和一个联网更新场景。实施时需要增加针对设置迁移、机器码生命周期和 Violet 更新源的行为测试，不能只依赖已有测试全绿。联网集成检查与可重复的本地测试分别执行；真实账号登录和发布包升级需要在合并代码完成后单独验收。

最终工作量判断：大范围的 CN 代码更新可以沿用，但人工工作集中且不可省略。优先解决“扫码功能在新架构中完整保留”和“Violet 不被更新成 Soil”两项，再做窗口文案等轻量修正。

**实施记录**

已在 `codex/merge-cn-2.4.9` 上完成 `d902b179` 的三方合并适配，16 个冲突全部解决，保留两条分支的提交历史。集成结果用于快进本地 Violet；不推送远端、不创建发布 tag。

- 使用 CN 新的 Flow/Service、`ILoginWorkflowUI` 和窗口结构；旧 Provider/Service 按新架构迁移并移除。
- 扫码保留“随机新建／共享／沿用已有账号”三种选择，统一由 `RequireDeviceProfileSetupForQRCodeLogin` 控制。账号预设仍复用 `DeviceProfilePresetId`，没有新增一套机器码存储。
- 旧配置缺少扫码字段时，一次性继承原开关；显式 false 保留，备份恢复也执行迁移。Debug 和全局禁用轮换开关独立保留。
- 沿用机器码时绑定扫码返回的实际账号；已有目标账号的备注、凭据、排序和轮换策略保留。二维码、后续快速登录和 Session 刷新继续使用同一快照；缺少必要快速登录凭据时终止保存。
- Violet 的 GitHub 更新器、发布 workflow、主题、图标保留；新标题栏和设置页使用 Violet 图标。排除指向上游账户的 CNB 分发脚本。
- 登录客户端支持注入测试替身；账号管理器支持隔离的数据目录和释放连接，回归测试不读取用户实际账号库。

完成的验证：

| 检查 | 结果 |
| --- | --- |
| 启动器 `ReleaseNoUpdate` 直接构建 | 通过，包含 ApkalluCaller 与 xdelta native 组件 |
| 全解决方案 `Release / x64` 构建 | 通过，0 个错误；保留现有第三方包兼容性、Rust 未使用项和上游元组命名警告 |
| 本地回归集 | 82 项通过，0 失败；包含本次新增 26 项测试 |
| 机器码流程 | 三种选择 × 新/已有账号、扫码开关独立性、账号来源与目标分离、取消/网络失败/凭据加密失败、快登与 Session 刷新、禁止轮换 |
| 设置迁移 | 旧值 true/false、显式新值、大小写兼容、只迁移一次、备份恢复、全新配置默认值 |
| Violet 更新器 | 模拟 MilkVio GitHub Releases，正式/预发布过滤、全量目标包和增量更新选择，无实际下载安装 |
| WPF 窗口 | 使用实际 Violet 资源加载扫码选择、设置、共享设备编辑窗口；检查三个机器码开关的独立绑定、已有账号选择状态，并生成离屏渲染图 |

可重复执行的验证命令：

```powershell
dotnet build src/XIVLauncher/XIVLauncher.csproj -c ReleaseNoUpdate --no-restore
dotnet build src/XIVLauncher.slnx -c Release -p:Platform=x64 --no-restore
dotnet test src/XIVLauncher.Test/XIVLauncher.Test.csproj -c ReleaseNoUpdate --no-restore -p:SkipNativeShims=true --filter 'Category!=Network&FullyQualifiedName!~UpdateOrchestratorTests'
```

测试报告：`src/XIVLauncher.Test/TestResults/cn-merge-regression.trx`。离屏图：`src/XIVLauncher.Test/bin/ReleaseNoUpdate/net10.0-windows10.0.19041.0/win-x64/UiValidation/`。构建结果：`src/bin/win-x64/XIVLauncherCN.exe`。这些生成文件不纳入源码提交。

验证范围说明：未使用真实账号登录，未启动游戏、执行 WeGame 抓取或安装升级包；原有联网测试未执行。离屏窗口验证覆盖资源加载与控件绑定，不等同于原生窗口交互验收。发布前仍需实际账号与安装包升级的手动回归。
