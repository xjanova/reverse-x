# Reverse X — เอกสารสั่งงานสำหรับ CluadeX

เจ้าของ: xjanova · วันที่สั่ง: 2026-10-13 · ผู้ส่งงาน: Claude (ทำหน้าที่ PM และดูแลตัวโปรแกรม CluadeX เท่านั้น)

**งานนี้ CluadeX ทำเองทั้งหมด**: ภาพ (ผ่าน ChatGPT) โค้ด repo CI ระบบไลเซนส์ อัปเดตอัตโนมัติ และ PR ขายบน xmanstudio
ไม่มีใครเขียนโค้ดของ Reverse X แทน ถ้าเครื่องมือของ CluadeX เองพัง ให้จดลง DEVLOG ตามจริงว่าพังตรงไหน แล้วหยุดรอ

## 1. สินค้า
- ชื่อ **Reverse X** · wordmark `REVERSE X` · tagline `INSIGHT STUDIO` · UI ภาษาไทยก่อน (มีอังกฤษด้วย)
- โปรแกรม Windows ที่เป็นหน้าจอสวยๆ ครอบ **REA** (https://github.com/morluto/rea, npm `rea-agents`, MIT, รุ่นตอนนี้ 6.4.0)
  ผู้ใช้ลากไฟล์/แอป/เว็บเข้ามา แล้วเห็นว่ามันทำงานยังไง (โค้ด C จำลอง, แอสเซมบลี, ใครเรียกใคร, แผนผังโมดูล/IPC ของ Electron, APK, .NET ฯลฯ)
  และถาม AI ให้สืบฟีเจอร์พร้อมหลักฐาน แล้วสร้างแบบเดียวกันในโปรเจกต์ของผู้ใช้ได้
- REA ไม่มี GUI: มีแต่ MCP server (stdio) + CLI (`rea <command> --json`) เราใช้ทั้งสองทาง ห้ามแก้โค้ดของ REA เอง
  อ่านวิธีเปิด MCP server, รายชื่อ tool และ `rea doctor` / `rea providers` / `rea capabilities` จาก README และ docs/ ของ REA
- ต้องมี Node.js 22.19+ / 24.11+ / 26+ (เครื่องเจ้าของมี 24.11.1) ถ้าไม่มี ให้หน้าจอบอกวิธีติดตั้ง
- ข้อควรรู้บนเครื่องเจ้าของ: Java 17 (Ghidra รุ่นใหม่ต้อง JDK 21), ยังไม่มี Ghidra/JADX, Hopper ไม่มีบน Windows, IDA ต้องมีไลเซนส์ของผู้ใช้เอง

## 2. กติกา
1. ทำงานใน `D:\Code\ReverseX` เท่านั้น (และ worktree ของ xmanstudio ในเฟสสุดท้าย) ห้ามแตะ repo อื่น ห้ามแก้ D:\Code\AntiX (อ่านได้)
2. Repo **public**: `github.com/xjanova/reverse-x` (เจ้าของสั่ง public) — ห้าม commit ความลับเด็ดขาด: private key สำหรับเซ็นอัปเดต, API key, token, `.env`
   กุญแจอยู่ใน GitHub Actions secrets และ `%USERPROFILE%\.xman-signing\` เท่านั้น เช็ค `git diff --cached` ก่อนทุก commit
3. REA เป็น MIT: ใส่ license/notice ของ REA ใน `THIRD_PARTY_NOTICES.md` ฟอนต์ใส่ไฟล์ OFL ด้วย
4. ในแอปต้องมีข้อความ "ใช้กับซอฟต์แวร์ที่คุณมีสิทธิ์วิเคราะห์" และไม่แจกเครื่องมือเถื่อน
5. xmanstudio: merge = deploy ขึ้น production → **เปิด PR เท่านั้น ห้าม merge ห้าม push main**
6. ห้ามสร้าง release tag จนถึงเฟส 6
7. จบแต่ละเฟส: build ผ่าน, เทสต์ผ่าน, commit + push ขึ้น repo, เขียน `DEVLOG.md` (ภาษาไทย) ว่าทำอะไร ติดอะไร เครื่องมือไหนของ CluadeX ไม่พอ/พัง
8. ข้อความ commit เป็นภาษาอังกฤษ

## 3. ดีไซน์
- ต้นแบบอยู่ที่ `E:\cluadex-bld\reverse-x-brief\design\` → `mock.html` (เปิดด้วย `?s=home|bin|ai&t=A|B`) + ภาพ `mock-*.png`
  ภาพพื้นหลังในโฟลเดอร์ `assets` ของ mock เป็นของ Anti X ใช้แค่อ้างอิง ต้องทำใหม่
- สไตล์ Millennium 3D / Frutiger Aero ตระกูลเดียวกับ Anti X: กระจก 3 ชั้น + gloss ด้านบน, ขอบโครเมียม, ปุ่ม aqua มีวงทอง, ธีมกลางคืน/ท้องฟ้าสลับได้
  ดูวิธีทำใน WPF จาก `D:\Code\AntiX\src\AntiX.App\Theme\` (Theme.xaml, Palette.Night.xaml, Palette.Sky.xaml), `Services/ThemeManager.cs`, ฟอนต์ใน `Assets/Fonts` (Kanit, Anuphan + OFL)
- ค้นสมองก่อน: "Anti X Millennium 3D redesign" (มีวิธีสั่ง ChatGPT ทำภาพ + กับดัก DOM + วิธีดึงไฟล์ภาพออก + กับดัก WPF palette swap)

## 4. ภาพ (ทำผ่าน ChatGPT ใน Chrome ของเจ้าของ ซึ่งล็อกอินไว้แล้ว)
- โลโก้ Reverse X: 4 แบบในแผ่นเดียว → เลือกแบบที่อ่านออกที่ 16–64 px, พื้นโปร่งใส (RGBA จริง)
- ไอคอน 3D อย่างน้อย 12 ชิ้น พื้นโปร่งใส: ภาพรวม, ผู้ช่วย AI, ไบนารี/ชิป, JS/Electron, Android, .NET, เว็บ, ฐานข้อมูล, หลักฐาน, ตั้งค่า, ฟังก์ชัน, เรื่องที่ยังไม่รู้
- พื้นหลัง 2 ภาพ 1536x1024: กลางคืน + ท้องฟ้า (พลังงานอยู่ซ้าย/บน กลางจอเงียบไว้วาง UI) ห้ามเหมือนของ Anti X
- ไฟล์ต้นฉบับเก็บที่ `design/art-src/`, ไฟล์ที่ใช้จริงที่ `src/ReverseX.App/Assets/` + `.ico` หลายขนาด

## 5. เทคนิค
- .NET 8 WPF แบบ Anti X: `ReverseX.App` (WPF), `ReverseX.Core` (ตัวคุยกับ REA ผ่าน MCP stdio + model), `ReverseX.Licensing`, `tests/`
- หน้าจอ v1: ภาพรวม · ไบนารี · แอป JS/Electron · ผู้ช่วย AI · หลักฐาน · ตั้งค่า (API key, ธีม, ภาษา, ไลเซนส์, อัปเดต)
- ผู้ช่วย AI: Claude API (ผู้ใช้ใส่ API key เอง เก็บด้วย DPAPI) ให้โมเดลเรียก tool ของ REA ได้ แสดงขั้นตอน/หลักฐาน/สิ่งที่ยังไม่รู้แบบในต้นแบบ
- ทดสอบ UI ด้วย UI Automation หรือ render ภาพ ห้ามใช้เมาส์จริงบนเดสก์ท็อปของเจ้าของ

## 6. ไลเซนส์ + อัปเดตอัตโนมัติ + ขาย (ทำตามแบบ Anti X ทุกอย่าง)
- อ่าน `D:\Code\AntiX\src\AntiX.Licensing`, `deploy/`, `.github/workflows/release.yml`, `tools/ReleaseSigner` และค้นสมอง:
  "xmanstudio Anti X product PR 461", "Owner rule every licensed xman program registers the PC online", "WinXTools license self-update"
- API: `https://xman4289.com`, slug `reverse-x`, ลงทะเบียนเครื่องออนไลน์ก่อนใช้, ทดลอง 14 วัน, Free/Pro, อัปเดตเซ็น ECDSA P-256, ดาวน์โหลดจาก xman4289.com เท่านั้น
- Release CI: tag `vX.Y.Z` → asset `ReverseX-X.Y.Z-win-x64.zip`
- ฝั่งร้าน: PR ไปที่ xjanova/xmanstudio แบบเดียวกับ PR 461 (migration ลงสินค้า, plans ใน config/licenses.php, วันทดลอง, route ดาวน์โหลด, หน้าสินค้า, เทสต์)
  ราคาใช้ตาม Anti X ไปก่อน (เดือน 199 / ปี 1990 / ตลอดชีพ 9900) แล้วเขียนใน PR ว่าให้เจ้าของแก้ราคา

## 7. เฟส (แต่ละเฟสสั่งแยกกัน ทำเฉพาะเฟสที่ถูกสั่ง)
1. Repo + โครงโปรเจกต์ + ทดสอบ REA ว่าเรียกได้จริงจาก C#
2. ภาพทั้งหมดผ่าน ChatGPT
3. ธีม + หน้าต่างหลัก + หน้าภาพรวม (สถานะเครื่องมือจาก REA จริง)
4. หน้าไบนารี + หน้า JS/Electron ต่อกับ REA จริง
5. ผู้ช่วย AI
6. ไลเซนส์ + อัปเดตอัตโนมัติ + release CI
7. PR ขายบน xmanstudio
