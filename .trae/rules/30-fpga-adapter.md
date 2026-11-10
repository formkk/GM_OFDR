# 采集适配器约束（LGA50 / 未来 FPGA）

## 代码归口

- 产品线设备接入：`GM-IX/src/Gmix.Acquirer/`（在线采集）与 `GM-IX/src/Gmix.Import/`（离线导入）；厂商调用只经 `Gmix.Vendor`。
- 预研平台适配器：实现 `OFDR.Contracts.IScanSource`，代码放 `ofdr_platform/src/OFDR.Infrastructure/<Adapter>/`；仅用于预研验证。
- 上层不得感知适配器存在；产品线与预研平台的适配器不得互相引用。

## 协议常量

- 协议常量集中到适配器目录下的单个协议文件（如 `FpgaBoardProtocol.cs` / `Qt2144Protocol.cs`）：magic、帧尾/长度字段、字节序、通道交织顺序、命令字、超时默认值。
- 其它文件不得出现字节偏移量或硬编码协议字符串。

## 生命周期与语义

- 预研 `IScanSource`：`Open` 建立会话、`StartStream` 开始数据流、`BeginChannel` 递增代次并丢弃旧代次帧、`TryTake` 超时返回 false、`Stop` 幂等、`DroppedFrames` 计数可读。
- GM-IX 采集进程：`open → configure → start → read → stop → close` 必须成对释放；`seg_count >= 2` 已知会挂起，禁止在产品路径启用。

## 线程与队列

- 接收线程与消费方解耦，使用有界队列形成背压，禁止无界堆积；队列满策略必须显式（丢最旧/阻塞）并有计数。

## 接入顺序

- 固定顺序：**mock 服务端 → 契约测试 → 真机冒烟**。未完成契约测试前不进行真机联调。
- 真机联调期间产生的不确定行为（时序、粘包、断连）必须回写对应 SDD 与 SRS 风险登记，不得只留在个人笔记里。

## 错误

- 错误必须可分类（连接/协议/数据/超时），并映射为可读消息上报 UI；禁止 `catch {}` 静默吞掉协议错误。
