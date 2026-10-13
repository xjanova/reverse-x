# Reverse X — INSIGHT STUDIO

## ไทย

**Reverse X** คือโปรแกรม Windows หน้าตาสวยที่ครอบ [REA](https://github.com/morluto/rea) (ตัววิเคราะห์ย้อนรอยซอฟต์แวร์)
ให้ลากไฟล์/แอปเข้ามาแล้วเห็นว่าโปรแกรมทำงานอย่างไร ถามผู้ช่วย AI เพื่อสืบฟีเจอร์พร้อมหลักฐาน

> ใช้กับซอฟต์แวร์ที่คุณมีสิทธิ์วิเคราะห์เท่านั้น

สถานะ: **เฟส 1** — โครงโปรเจกต์ + ไลบรารี `ReverseX.Core` ที่คุยกับ REA ผ่าน MCP (stdio) และ CLI ได้จริง
หน้าต่างหลักยังเป็นเพียงหน้าต่างเปล่า (ธีมและหน้าจอจริงอยู่เฟสถัดไป)

### ต้องมีอะไร
- Windows 10/11 x64
- .NET 8 SDK (สำหรับ build)
- Node.js 22.19+ / 24.11+ / 26+ (REA ใช้งาน; ถ้าไม่มี เทสต์ที่เรียก REA จะถูกข้ามพร้อมเหตุผล)
- เครื่องมือวิเคราะห์เจาะลึก (Ghidra 12.1.x / IDA / Hopper) เป็นทางเลือก — งาน .NET และ JS/Electron ใช้ได้โดยไม่ต้องมี

### วิธี build / ทดสอบ
```
dotnet build ReverseX.slnx
dotnet test ReverseX.slnx
```
ครั้งแรกเทสต์ integration จะดาวน์โหลด `rea-agents@6.4.0` ผ่าน npx (ใช้เวลาสักครู่)
ตั้ง `REVERSEX_SKIP_REA=1` เพื่อข้ามเทสต์ที่เรียก REA, ตั้ง `REVERSEX_WRITE_SAMPLES=1` เพื่อสร้างตัวอย่างใน `docs/samples/`

### ไลเซนส์
Source-available — อ่านโค้ดได้ แต่ห้ามแจกจ่าย/ขายต่อ/ใช้เชิงพาณิชย์โดยไม่มีไลเซนส์ ดู [LICENSE](LICENSE) และ [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

## English

**Reverse X** is a polished Windows front-end for [REA](https://github.com/morluto/rea), a reverse-engineering MCP server.
Drop in a binary, app or site, see how it works, and ask an AI assistant to investigate features with evidence.

> Use only on software you are authorized to analyze.

Status: **phase 1** — solution skeleton plus `ReverseX.Core`, which talks to REA over MCP (stdio) and its CLI.
The main window is a placeholder; theme and real screens come in later phases.

### Requirements
- Windows 10/11 x64
- .NET 8 SDK (to build)
- Node.js 22.19+ / 24.11+ / 26+ (REA runtime; integration tests are skipped with a reason if missing)
- Deep native engines (Ghidra 12.1.x / IDA / Hopper) are optional — .NET and JS/Electron analysis work without them

### Build & test
```
dotnet build ReverseX.slnx
dotnet test ReverseX.slnx
```
The first integration run downloads `rea-agents@6.4.0` through npx. Set `REVERSEX_SKIP_REA=1` to skip REA-backed tests;
set `REVERSEX_WRITE_SAMPLES=1` to regenerate `docs/samples/`.

### License
Source-available: you may read the code, but redistribution, resale and commercial use require a license. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
