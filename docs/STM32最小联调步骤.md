# STM32 最小联调步骤（lab202 验证链路）

> 目标只有一个：让上位机收到字符串 `lab202`。
> 上位机 = TCP 服务端，监听 **9000** 端口；STM32 = 客户端，主动连接。

## 第 1 步：连上上位机

用你工程里的 TCP 客户端连到上位机（IP 问上位机同学，端口 9000）。BSD socket 示例：

```c
int sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
struct sockaddr_in addr = {0};
addr.sin_family = AF_INET;
addr.sin_port = htons(9000);
addr.sin_addr.s_addr = inet_addr("192.168.1.100");   /* 换成上位机实际 IP */
if (connect(sock, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
    /* 连接失败：检查 IP / 端口 / 防火墙 */
}
```

> 如果你的工程用的是 lwIP netconn / raw API，把上面换成对应连接调用，得到可发送的连接句柄即可。

## 第 2 步：直接发这 12 个字节（不用算 CRC）

字节流我已经算好、校验过了，原样发：

```c
const uint8_t hello[12] = {
    0xAA, 0x54, 0x0C, 0x00, 0x6C, 0x61, 0x62, 0x32, 0x30, 0x32, 0x33, 0xC2
};
send(sock, hello, 12, 0);   /* 一次发满 12 字节，不要拆 */
```

含义：`AA 54` = 文本帧同步头，`0C 00` = 整帧 12 字节（小端），`6C 61 62 32 30 32` = ASCII `lab202`，`33 C2` = CRC（0xC233，小端）。

## 第 3 步：看上位机

上位机「数据包调试」窗口应显示：

```
原始十六进制：AA 54 0C 00 6C 61 62 32 30 32 33 C2
右侧解析：text = lab202，crc16 = OK
```

看到这行，链路就通了。

---

## 通了以后，换成通用函数（任意文本都能发）

```c
/* CRC16/CCITT-FALSE：poly 0x1021，init 0xFFFF，非反射，输出不异或 */
uint16_t crc16_ccitt_false(const uint8_t *data, uint16_t len)
{
    uint16_t crc = 0xFFFF;
    for (uint16_t i = 0; i < len; i++) {
        crc ^= (uint16_t)data[i] << 8;
        for (uint8_t b = 0; b < 8; b++)
            crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021)
                                 : (uint16_t)(crc << 1);
    }
    return crc;
}

void send_text(int sock, const char *text)
{
    uint16_t n = (uint16_t)strlen(text);
    uint16_t length = (uint16_t)(4 + n + 2);
    uint8_t frame[128];
    frame[0] = 0xAA; frame[1] = 0x54;
    frame[2] = (uint8_t)(length & 0xFF);
    frame[3] = (uint8_t)(length >> 8);
    memcpy(&frame[4], text, n);
    uint16_t crc = crc16_ccitt_false(frame, (uint16_t)(length - 2));
    frame[length - 2] = (uint8_t)(crc & 0xFF);
    frame[length - 1] = (uint8_t)(crc >> 8);
    send(sock, frame, length, 0);
}
/* 以后：send_text(sock, "lab202"); */
```

## 三条纪律

1. **整帧一次 `send()`**，不要拆成多次小包。
2. **不要往这条 TCP 连接里 `printf` / 发 ASCII 调试日志**，会被上位机当垃圾丢弃，还会打乱帧同步。
3. 上位机如果没反应，先确认：上位机「数据包调试」窗口已启动、端口 9000、你的 IP 和端口填对了。

协议完整版见同目录 `上位机与STM32通信协议v0.1草案.md`（文本帧在 3.1 节）。
