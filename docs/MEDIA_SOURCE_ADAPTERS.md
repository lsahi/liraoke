# 音源搜索与导入 Adapter（2026-10-30）

## 产品范围与当前状态

在 `feature/ai-integration` 开发。项目素材区“找歌”打开独立窗口，按歌曲／歌手搜索网易云、QQ、汽水或聚合搜索；每个来源首批最多 30 条。结果保留来源 ID、原歌名（包括 Live/Remix 等）、歌手、专辑和时长，不合并同名歌曲，不自动替换为翻唱。

选中歌曲后主动获取音频，试听或确认导入。生成本地 48 kHz 双声道 PCM WAV，调用现有 WorkspaceService 复制至项目；更换项目歌曲前处理歌词草稿和未应用打点。既有歌词、演唱单元和已确认时间点不因找歌而替换。导入后仍需显式保存工程。

不导入逐字歌词；歌词生成、读音修正、音频对齐继续作为独立业务和工作流规划。已接入 search_music / open_music_search / import_music；AI 查询直接在聊天内校验并展示首个可播放结果，用户点击播放或通过 Approve 导入并保存，不弹出找歌窗口。素材区手动找歌入口保留原窗口。模型只获得脱敏元数据和临时 selectionId。

已实际验证三个来源的公开元数据搜索。使用用户此前用于测试的 Daisuke 对网易／QQ 做了真实下载与 FFmpeg 解码：分别为 127.733 秒、127.844 秒，时长校验通过，临时音频随后清理。工作区复制与迁移使用自生成音频离线验证。没有使用私人账号验证会员权限，单首成功不代表所有曲目可获取。汽水整曲尚未联调，依赖项详见下文。

## 分层与新增音源

```
WPF MusicSearchWindow
  -> MusicSourceRegistry（来源注册、聚合、错误隔离）
     -> IMusicSourceAdapter
        -> NeteaseMusicSourceAdapter
        -> TencentMusicSourceAdapter
        -> QishuiMusicSourceAdapter
  -> MusicAudioTransfer（下载、探测、解码、时长校验、临时文件生命周期）
  -> WorkspaceService.ImportMediaAsync（项目相对路径、复制、素材登记）
```

- Application 契约不依赖 WPF、Pi、Cordis 或 Node。每个来源实现独立 Adapter；本版三个非官方适配通过专用 Node JSONL 工作进程调用选定的 Meting 提供方源码，不启动 OpenMusic 房间服务器、Redis、管理后台或账号池。
- Search、Preview、ImportAudio、Account 是独立能力。未来只开放目录或官方播放器的来源不能据此声明可下载完整音频。接入方应在 UI 与服务两层遵守能力。
- 新来源可以直接用 .NET HttpClient 实现接口，不要求继承 LocalMusicSourceAdapter。涉及 JS 库时才扩展 MusicHost。`MusicSourceDescriptor.Authentication` 区分 None/Cookie/OAuthPkce/ApiKey。
- Registry 按 provider ID + track ID 去重。单一来源故障返回局部警告；取消聚合请求会取消所有来源。
- Worker 一次请求一个私有子进程，凭据通过 stdin 传递，不放命令行。超时／取消结束进程树，供应商原始日志不转发到 UI 或应用日志；只有白名单错误码跨边界。

## 账号和官方认证

首版提供每个音源自己的个人 Cookie 输入。音乐凭据和可选汽水 signer 地址以 Windows CurrentUser DPAPI 加密写入应用根目录 user-preferences.json，使用现有原子更新／备份机制，保留 AI 配置、样式和角色。音乐服务不读取 AI 凭据。

未来有官方 API 的音源应新增官方 Adapter，并按官方支持的认证流程接入：

- Spotify：[授权文档](https://developer.spotify.com/documentation/web-api/concepts/authorization)；桌面 public client 优先 Authorization Code + PKCE。目录搜索、播放控制与完整音频文件导入是不同能力；没有正式下载许可时只提供搜索／转到来源。
- SoundCloud：[API 指南](https://developers.soundcloud.com/docs/api/guide)；需要注册应用及支持的 OAuth 流程，按 track 的播放／下载许可返回能力，不把 streaming URL 当作导出授权。需要 client secret 的步骤不能把全体用户共享的 secret 放在桌面包中。
- 认证应独立于业务 Adapter：登录、注销、刷新、过期通知及授权 scope；凭据存储可沿用 DPAPI 封装，但不把 Cookie 字段假装成 OAuth token。回调要校验 state/PKCE，使用受限本机回调或供应商支持的方案。

## 汽水接入的实测与依赖边界

OpenMusic/Meting 不等于公开官方 API。固定本次研究源码为 [Meting-API 073256842034beb8a0445d3a36cbef9f9d530e1b](https://github.com/qq01-hub/Meting-API/tree/073256842034beb8a0445d3a36cbef9f9d530e1b)，其 package 版本为 3.4.2。

- 汽水公开搜索 `api-vehicle.volcengine.com/v2/search/type` 可返回 name/artist、秒级时长；网易／QQ 使用 title/author、毫秒级时长。归一化需要来源专属测试，不能只按表面同名接口推断结构。
- 此版本整曲 `track_v2` 明确要求 Cookie 和 X-Helios/X-Medusa signer。puppeteer 的扫码 Web 签名与 PC 整曲签名不是一回事，单纯增加浏览器依赖不能完成整曲接入。
- 用户已确认首版采用自行配置可信 signer 完整 URL；仅接受 HTTPS 或本机回环 HTTP。签名负载排除 Cookie，不预置公共 signer 或共享秘密。配置入口提供“检查签名服务”，只用合成请求验证连接与响应格式，不发送 Cookie、不验证签名有效性或整曲权限，也不自动保存。signer 须兼容当前 Meting 契约：POST JSON { deviceId, url, headers }（headers 为扁平键值数组），响应 { ok: true, "X-Helios": "…", "X-Medusa": "…" }；其他签名服务需要单独 Adapter，不能只替换地址。真实 signer 联调尚未完成；未配置时显示明确提示，不伪装成空搜索结果。
- [libmssdk](https://github.com/sodahub-org/libmssdk) 是可研究的本地路线：MIT 包装器调用用户已安装汽水客户端里的 bdms.node/metasecml.dll。其文档明确这些官方二进制不随开源仓库分发。首版不走此路线；目前没有扫描、加载或分发这些二进制。
- 因此当前不额外携带 Chromium；工作进程复用已经提供给 AI 的 Node 22.23.4，独立安装锁定的 nanoid 4.0.2、esbuild 0.28.2。不要为了“汽水支持”盲目打包整套浏览器。

## 下载与工程生命周期

- Resolve 返回临时 URL、音质、试听标记和（汽水）流解码材料，只在内存中使用；不保存在项目、不提供给模型。
- 下载不携带 Cookie。每次重定向重新校验 URL；生产传输只连接公开地址，拒绝本机、私网、用户信息 URL、非 HTTP(S) 和非标准端口，最多 5 次重定向。Socket 直接使用已校验 DNS 地址，避免二次解析改变目的地。
- 单曲下载上限 256 MB，处理上限 30 分钟、5 分钟任务超时。FFmpeg/ffprobe 禁止网络协议；检查有效音轨，输出 WAV。用户取消／关闭窗口会取消任务，失败会清理本次私有临时目录；进程异常退出后的孤立临时文件目前未实现自动清扫。
- 有明确试听标记、缺少原曲时长、或实际时长与目录时长差异超过 max(3 秒, 2%) 时只允许试听。时长匹配是截断检查，不是版权授权证明或内容指纹匹配，也不保证检测所有伪装片段。
- 用户确认后才使用现有 ImportMediaAsync。音频旁的 `.source.json` 仅记录来源、曲目 ID、原始标题／艺人／专辑、实际时长／音质／导入时间；不保存 URL、Cookie、密钥。项目 schema 不变。

## 构建、许可与验证

`scripts/bootstrap-music.ps1` 复用 AI Node 引导，npm ci 锁定依赖，然后打包工作进程；`-SkipNodeBootstrap` 可复用已准备好的 Node。CI 与打包脚本已接入。包内不依赖机器全局 npm、Node、Python 或 Redis。

选取的 Meting 源码及修改清单见 `src/Liraoke.MusicHost/vendor/meting/PROVENANCE.txt`。没有包含 Folia/QRC AGPL 代码、歌词模块或浏览器安全资源。上游 package.json 声明 MIT，但该快照无根 LICENSE；**对外分发前仍需补齐并确认所选源码的版权／许可文本**，不能据“整个项目开源”推定所有二进制与媒体均可重分发。当前只是开发分支，没有发布包。

离线测试覆盖：结果归一化／大整数 ID／录音版本保留、聚合故障／取消、凭据与 AI 设置共存、拒绝私网、试听与时长策略、真实 FFmpeg 解码与工程迁移、WPF 搜索选择／取消／窄窗口。网络搜索作为手动烟雾验证，不进入 CI。

## 为视频搜索导入保留的经验

先沿用来源身份、认证与能力声明，以及公开下载传输和临时文件生命周期；视频需要自己的 VideoTrack、流选择、分辨率／帧率／时长／音轨信息与合法下载能力，不将 MusicTrack 强行塞满可选字段。最终进入现有 Video/Image 导入事务。

未来可提取 MediaSourceDescriptor / ISourceAuthentication / MediaDownloadService 三个公共模块，让音频和视频 Adapter 共享。拆分应跟随第二种实际媒体实现，避免现在制造未验证的万能接口。禁止把供应商的网页播放权限推导成离线视频导出权限；分页、限流、配额和登录更新都必须按源设计。
