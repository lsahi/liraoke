# 练歌房硬件监听设备表

第一批面向家用录音、直播和练唱。按型号名计 19 项（含用户实测的早期 VT-3），同型号不同代合并。这个数量仅描述本表。Windows 驱动若只报告通用名称，按端点的设备关系给出较低置信度的建议。

| 型号 | 硬件监听依据 |
|---|---|
| Roland VT-3、VT-4 | 人声处理器将话筒输入送到 PHONES。VT-3 由项目用户实听确认。[VT-3 手册](https://static.roland.com/assets/media/pdf/VT-3_OM.pdf) · [VT-4 手册](https://static.roland.com/assets/media/pdf/VT-4_eng02_W.pdf) · [2018 年公告](https://www.roland.com/us/company/press_releases/2018/ROLAND-ANNOUNCES-VT-4-VOICE-TRANSFORMER/) |
| Roland Rubix22、24、44 | 2017 系列各机型有不同的 DIRECT MONITOR 控件。[参考手册及信号通路图](https://static.roland.com/assets/media/pdf/Rubix_reference_eng01_W.pdf) |
| Yamaha AG01、AG03MK2、AG06MK2 | 2022 系列。关闭监听端的 MIX MINUS，让人声与 USB 播放一起进入耳机。[上市公告](https://www.yamaha.com/ja/news_release/2022/22022802/) · [控制面板说明](https://manual.yamaha.com/pa/live_streaming/ag03_06/en-US/8410785419.html) · [含 AG01 的规格对照](https://usa.yamaha.com/products/proaudio/live_streaming_gaming/ag/specs.html) |
| Focusrite Scarlett Solo、2i2、4i4、8i6、18i8、18i20、16i16、18i16 | 覆盖第三、第四代出现的型号名；硬件按钮和控制软件随机型和代际变化。[官方直听支持列表](https://support.focusrite.com/hc/en-gb/articles/207546305-What-is-direct-monitoring) |
| MOTU M2、M4、M6 | 输入通道 MON 提供本机监听。短型号要求有 MOTU 品牌上下文。[M 系列](https://motu.com/en-us/products/m-series/) · [M6 上市及硬件监听说明](https://cdn-data.motu.com/marketing/motu_products/audio_interfaces/m6/motu-m6-pr.pdf) |

## 配对与选择

打开窗口时枚举活动端点。优先比较非系统全局的设备容器和物理父设备实例，明确冲突的容器禁止配对。已知型号端点缺少身份信息时仅提示核对实物。未知同设备 USB 输入和输出可以试听；Realtek 模拟候选排除数字播放和立体声混音。软件无法验证耳机接线、设备监听开关或驱动的回放能力。

推荐菜单只列候选。点击“使用所选设备”才切换练歌房播放输出并关闭软件耳返。保持 Windows“侦听此设备”和软件耳返关闭，耳机里能听见话筒和播放的伴奏后即完成配置。
