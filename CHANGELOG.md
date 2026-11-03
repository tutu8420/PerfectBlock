# 版本记录

## 0.3.3

参数调整（默认值改成作者实测最顺手的一套，老玩家已生成的 cfg 不受影响）：

- `Enable Auto Block` 默认开启
- `Detect Range` 25 → 5 米
- `Log Attempts` 默认开启
- `Min Samples` 3 → 5
- `Hold After Perfect (ms)` 0 → 500
- `Show Vanilla Numbers` 默认开启
- `Lead Ms 70` / `Press Tail Ms 150` / `Base Font Size 27` / `Big Hit Scale 2.4` / 数字位置 `0.2,0.6` 与 `0.7,0.6` 保持上一版

## 0.3.2

- 文档：README 封面图改到 jsDelivr（固定 commit 地址），解决 GitHub / Thunderstore 页面上演示 GIF 裂图的问题（原图 19–38MB，超出图片代理与 CDN 的 20MB 上限，已重新编码到 2–4MB）
- 文档：标题标注实测成功率，中英双语补齐实测数据与「关闭切换格挡最佳」的说明
- 无功能改动

## 0.3.1

首次公开版本。

- **学习**：hook 非玩家实体的 `OnAttackTrigger`，按 `(预制体, 动画状态)` 记录命中瞬间的动画进度，存 `BepInEx/config/PerfectBlock/learned.json`；样本数与离散度达标后才开始自动格挡
- **自动格挡**：命中点前 `Lead Ms`（70）按下，命中后按满 `Press Tail Ms`（150）；固定值，不做自适应（自适应会把提前量推到 0.25 秒窗口之外）
- **两种格挡模式**：自动识别游戏的「切换格挡」；长按模式实测 91% 完美率、0 漏挡，切换模式约 4% 漏挡
- **手动格挡取消挥刀**（等价 AttackCancel）：只对你手动按下的格挡生效，自动格挡不会打断挥击
- **Reliable Perfect Block**：只在完美格挡那一下忽略「大伤害破防」规则
- **完美提示**：屏幕中央 `PERFECT BLOCK !!!`，连续触发向上叠加
- **战斗数字**：`武器图标 + 数字 + (伤害类型)`，承伤红色在左、输出黄色在右；位置/字号/字体/武器图标可调，带常显测试开关
- **中英双语** yml，按游戏语言自动加载；配置项带完整说明
- 修复早前版本：`Release Delay` 语义写反导致每次格挡后主动卸防 250ms；战斗数字取血量差值的位置错误（`Character.Damage` 只发 RPC，血量在 `Character.ApplyDamage` 里改）
