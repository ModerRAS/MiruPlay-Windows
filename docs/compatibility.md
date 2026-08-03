# MiruPlay Windows 兼容状态

状态含义：`完成` 已有实现和自动化验证；`进行中` 已有垂直切片但契约尚未完整；`待实现` 尚未提供用户入口。

| 功能 | 状态 | 当前边界 |
|---|---|---|
| MLIP v1-v4 读取 | 完成 | 只读；校验版本、表、capability、路径安全和 v4 artwork pack 哈希/边界；v1-v3 保留 path 行为 |
| 海报墙、搜索、详情 | 完成 | Anime/Drama 媒体源；桌面响应式布局 |
| 本地视频播放 | 完成 | 支持固定 mpv/默认播放器、命名管道状态和暂停/继续/停止/跳转/倍速控制 |
| 内封/外挂字幕与语言偏好 | 完成 | 通过 mpv `track-list`/`sid` 枚举、自动偏好、手动选择和关闭；WPF 与 WebControl/WebUI 共用会话状态 |
| 播放进度、继续观看、下一集 | 完成 | 独立 state.db、15 秒保存、继续观看入口、90% 完成判定和下一集动作 |
| 独立目录识别 | 进行中 | Local/WebDAV/SMB 普通目录可手动、增量、可取消地扫描到 Windows SQLite 索引；自动扫描和间隔设置尚未实现 |
| Local/WebDAV/SMB 来源管理 | 进行中 | 三类来源均支持 MLIP 与 DIRECTORY 的 WPF/WebUI CRUD、验证、扫描、字幕与播放；远程凭据使用 CurrentUser DPAPI；自动扫描仍缺失 |
| Anime/Drama 模式 | 进行中 | 模式持久化并可由 WPF、WebAPI 和 WebUI 切换；目录扫描尚未接入完整 Anime/Drama 元数据解析流程 |
| Bangumi/TMDB 补充元数据 | 完成 | MLIP 是唯一元数据权威；支持只读搜索、加密 Token、Bangumi 身份验证、作品收藏读写、完整分页的分集状态读取，以及自然播放完成后标记单集“看过” |
| WebControl/WebUI | 完成 | 内嵌响应式 WebUI 已覆盖令牌认证、真实媒体库/海报/详情/播放/字幕、Local/WebDAV/SMB CRUD/测试/扫描与目录浏览、Anime/Drama 模式、CloudDrive2/RSS 配置/运行/预览、Bangumi/TMDB、播放设置和令牌轮换；本地与认证 WebDAV 海报均通过受保护代理提供 |
| CloudDrive/RSS | 完成 | 支持 RSS 订阅 CRUD、CloudDrive2 配置、端点绑定的 DPAPI 凭据、官方 gRPC 登录/Token 权限验证、根目录约束浏览、离线提交、RSS/Atom 预览、过滤与去重、.torrent staging、周期调度、认证恢复、媒体整理和成功门控的 WebDAV 回扫 |
| 安装包与发布 | 完成 | 提供自包含 win-x64 便携包、非管理员 Inno Setup 安装包、符号包、manifest、SHA-256、许可清单、固定 mpv 来源、可选 Authenticode、强制真实 mpv CI 和稳定 SemVer GitHub Release 发布 |

Windows 与 Android 不共享可执行源码；MLIP、WebControl DTO、枚举值和 fixture 是一致性依据。Android 专属播放器诊断和 APK 安装接口将提供明确的 Windows unsupported 结果，不伪装成功。
