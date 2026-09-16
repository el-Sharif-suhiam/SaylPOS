# سيل · نقطة البيع — Sayl POS

**سيل (Sayl)** نظام نقاط بيع (POS) متكامل يعمل محليًا (Offline-first) للأسواق والمتاجر والصيدليات والوكالات الطبية. مبني على **C# / WinForms / .NET 10** مع **SQL Server**، بواجهة عربية (RTL) افتراضية قابلة للتبديل للإنجليزية، ومناسب للشاشات اللمسية، مع تفرّع حقيقي (جهاز رئيسي + شاشات عبر الشبكة المحلية).

**Sayl** is a complete, offline-first Point-of-Sale system for retail stores, pharmacies and medical agencies. Built on **C# / WinForms / .NET 10** with **SQL Server**, an Arabic-first (RTL) interface switchable to English, touch-ready, and a true multi-terminal model (Main host + Terminals over LAN).

---

## الفكرة / The Idea

نظام بيع تُدار بياناته **على أجهزة المحل نفسه** — المنتجات والعملاء والفواتير والأرصدة والجلسات في قاعدة بيانات محلية، بلا اعتماد على خادم خارجي أو سحابة. هذا يمنح التاجر:

> - **ملكية كاملة لبياناته** مجزأة على أجهزة محله فقط.
> - **استمرارية عمل بدون اتصال بالإنترنت** إطلاقًا.
> - **نسخ احتياطي واستعادة** بأمر واحد — يوميًا وعند كل إغلاق.
> - **تفرّع محلي**: جهاز رئيسي يستضيف القاعدة، وشاشات/أكشاك إضافية تتصل عبر الشبكة المحلية برمز وصول.

A store-managed POS where **all business data lives on the customer's own machines** — no external server, no forced cloud. The merchant gets full data ownership, uninterrupted offline operation, one-command backup/restore, and an optional LAN deployment (one **Main** host plus extra **Terminal** screens).

---

## المزايا الرئيسية / Key Features

- **شاشة الكاشير (POS)**: بحث حي فوري، إضافة سريعة للسلة، مسح باركود (مع أو بدون Enter)، فواتير معلّقة واستئناف، جلسة درج يومية، دفع متعدد (نقد/شبكة/آجل) مع حساب الباقي، طباعة حرارية ESC/POS تلقائية، وشاشة زبون ثانوية اختيارية.
- **لوحة التحكم (Main)**: لوحة مؤشرات، المنتجات، المخزون (استلام/جرد/تسوية)، العملاء، الموردون، الفواتير، المصروفات، التقارير، الموظفون والرواتب، المستخدمون والأدوار، الإعدادات، والسنة المالية.
- **مرتجعات ذكية**: مرتجعات العملاء والموردين مرتبطة بالفواتير الأصلية، استرداد نسبي دقيق، ونقد المرتجعات موجّه لجلسة الدرج الفعلية.
- **مخزون FEFO**: البيع يسحب الدفعات الأقرب للانتهاء أولًا (First-Expiry-First-Out) مع تتبع الدفعة في كل بند فاتورة وحركة مخزون.
- **الرواتب**: أجور ساعية/يومية/شهرية من بيانات الشفتات، محمية بسنة مالية مغلقة.
- **الصلاحيات بالأدوار**: مدير / كاشير / محاسب بنمط bitmask، وتمنحها الرتب العليا.
- **التقارير**: مبيعات اليوم، حالة المخزون، الربح (COGS)، الأكثر مبيعًا، الدفعات المنتهية، التقارير الدورية، وتصدير Excel وPDF.
- **الترخيص بلا اتصال**: تفعيل لكل جهاز على حدة (Hardware ID) بتوقيع RSA-2048.
- **ثنائي اللغة**: عربي RTL (افتراضي) + إنجليزية.

- **Cashier screen (POS)**: instant live search, one-tap cart add, barcode scan (with or without Enter), suspend/resume invoices, daily drawer session, mixed payment with change, automatic ESC/POS thermal printing, optional customer display.
- **Admin dashboard**: KPIs, products, stock (receiving/count/adjust), customers, suppliers, invoices, expenses, reports, employees & payroll, users & roles, settings, fiscal year.
- **Smart returns**: customer & supplier returns linked to original invoices, precise pro-rata refunds, cash routed to the actual drawer session.
- **FEFO inventory**: sells oldest-expiring batches first, with batch tracing on invoice lines and stock movements.
- **Payroll**: hourly/daily/monthly wages from shift data, guarded by closed fiscal years.
- **Role-based permissions**: Admin / Cashier / Accountant via bitmask, delegated by superiors.
- **Reports**: day sales, stock status, profit (COGS), top sellers, expiring batches, periodic reports, Excel/PDF export.
- **Offline licensing**: per-device activation (Hardware ID) with RSA-2048 signatures.
- **Bilingual**: Arabic-first RTL (default) plus English.

---

## وحدات النظام / Modules

| الوحدة                    | Module            | المسؤولية                               |
|---------------------------|-------------------|-----------------------------------------|
| شاشة البيع                | Sales screen      | بيع، مرتجعات، معلّقات، دفع، طباعة        |
| لوحة المؤشرات             | Dashboard         | ملخص المبيعات والأرباح والمخزون          |
| المنتجات                  | Products          | إدارة + باركود + دفعات                   |
| المخزون                   | Inventory         | استلام، جرد، تسوية، تتبع دفعات           |
| العملاء / الموردون        | Customers/Suppliers| حسابات فورية + حدود ائتمان               |
| الفواتير                  | Invoices          | بيع/شراء/مرتجع + تفاصيل + طباعة          |
| المصروفات                 | Expenses          | تسجيل وتصنيف المصروفات                   |
| الموظفون والرواتب         | Employees & Payroll| مطابقة + شفتات + أجور                   |
| المستخدمون والأدوار       | Users & Roles     | حسابات + صلاحيات مقطّعة                  |
| التقارير                  | Reports           | مبيعات/مخزون/ربح/دورية                   |
| جلسات الدرج               | Cash sessions     | فتح/إغلاق + تقرير Z                      |
| السنة المالية             | Fiscal year       | إغلاق/فتح + حراسات                       |
| الإعدادات                 | Settings          | معلومات المتجر والضريبة والطباعة والنسخ  |

---

## شاشات التطبيق الرئيسية / Main Screens

| الشاشة       | Screen       | الوصف                                                              |
|--------------|--------------|---------------------------------------------------------------------|
| تسجيل الدخول | LoginForm    | اختيار المستخدم + كلمة المرور، توجيه حسب الدور إلى POS أو اللوحة     |
| البيع        | PosForm      | شاشة الكاشير اللمسية: بحث حي، سلة، معلّقات، دفع، طباعة تلقائية      |
| اللوحة       | MainForm     | مكوّن من 13 قسمًا برأية صلاحيات، يستضيف الوحدات في إطار واحد        |
| الإنشاء الأول| OwnerSetupForm| شاشة إنشاء «حساب المدير» عند أول تركيب (لا حسابات افتراضية)        |
| التفعيل      | ActivationForm| إدخال رخصة لكل جهاز                                                 |

---

## المعمارية / Architecture

```
+---------------------------------------------------------------+
|                    Sayl.UI (WinForms UI)                      |
|  PosForm / MainForm | أقسام UserControl | DialogForms          |
|  Infrastructure: AppLang (ar/en) | AppTheme | AppNavigator      |
|  AppClock (توقيت المحل UTC+3)                                  |
+-------------------------------+-------------------------------+
        | user actions             | Result<T> / DTO / ViewModel
        v                          v
+--------------------------------------------------------------------------------+
|        Sayl.Core (Sayl_DataAccess) — طبقة الأعمال والبيانات                     |
|  SaylDbContext (EF Core) | 24 خدمة أعمال (Services) | سجل تدقيق (SystemLogs)    |
+-------------------------------+-------------------------------+
        | EF Core / SQL           | Audit
        v
+--------------------------------------------------------------------------------+
|   SQL Server (SaylDB) — قاعدة محلية مثبّتة على جهاز المحل                       |
|   فهارس فريدة | قيود مالية CHECK | طوابع زمنية UTC | حراسات FEFO والجلسات      |
+--------------------------------------------------------------------------------+
```

- **فصل تام**: كيانات EF لا تعبر إلى الواجهة إطلاقًا — كل التبادل عبر `Result<T>` + نماذج نقل (DTO/ViewModel).
- **حقن تبعية مركزي**: مشروع `DependencyInjection` يسجّل القاعدة وجميع خدمات الأعمال معًا (`AddSaylServices`).
- **ساعة موحدة `AppClock`**: التخزين بالـ UTC، والعرض بتوقيت المحل الثابت (UTC+3) — لا زحف زمني عبر الأجهزة.
- **قرارات محاسبية على مستوى الخدمة**: حراس مالية، توليد رواتب، إغلاق سنوات مالية — كلها داخل طبقة الأعمال لا الواجهة.
- **اختبار متكامل (E2E)**: الهارنس `consol app` يقود نفس الخدمات على قاعدة حية (87 اختبارًا عبر مسارات محاكاة هجوم).

English mirror:

- **Clean separation**: EF entities never cross the UI boundary — all traffic is `Result<T>` + DTO/ViewModel.
- **Central dependency injection**: the `DependencyInjection` project wires the DbContext and all 24 business services together (`AddSaylServices`).
- **Unified `AppClock`**: storage in UTC, presentation in the store's fixed local time (UTC+3) — no time drift across machines.
- **Accounting decisions live in the service layer**: financial guards, payroll generation, fiscal-year close — none leak into the UI.
- **E2E harness**: `consol app` drives the same services against a live database (87 checks across attacker-style paths).

---

## حزمة التقنيات / Technology Stack

| الطبقة          | Layer             | التقنية                                                       |
|-----------------|-------------------|---------------------------------------------------------------|
| الواجهة         | UI                | WinForms (.NET 10), مزخرفة يدويًا دون Designer، RTL/Dark theme |
| الأعمال/البيانات | Business/Data     | EF Core 10 + SQL Server، نمط Result + DTO/VM                  |
| الأمان          | Security          | BCrypt.Net (كلمات المرور)، RSA-2048 (الترخيص)، DPAPI (الرخصة) |
| التقارير        | Reporting         | QuestPDF (PDF)، ClosedXML (Excel)، LiveChartsCore (الرسوم)    |
| التثبيت         | Installer         | Inno Setup 6 + SQL Server 2022 Express مورد محليًا             |
| الوحدة الاختبارية | Testing harness  | `consol app` — E2E على قاعدة حية                              |

---

## قاعدة البيانات / Database

23 جدولًا في `SaylDB.sql` تغطي: الأطراف (عملاء/موردون)، الموظفون، الأدوار، المستخدمون، المنتجات، الدفعات (Batches)، المخزون، الفواتير وبنودها، المدفوعات، حركات المخزون، المصروفات، سجل التدقيق، جلسات الدرج، الإعدادات، حركات النقد، الشفتات، سجلات الرواتب، والجرد.

أبرز نقاط التصميم:

- **طوابع Zمن UTC** في كل الجداول، مع عرض ثابت بتوقيت المحل.
- **قيود مالية CHECK**: مبالغ موجبة، صافٍ غير سالب للبيع/الشراء (المرتجعات سالبة بالتصميم)، أرصدة عملاء/موردين غير سالبة.
- **فهارس فريدة**: باركود المنتج، بريد الطرف، مستخدم واحد لكل موظف، جلسة درج واحدة مفتوحة لكل مستخدم، دفعة (منتج+رقم)، راتب (موظف+فترة).
- **توجيه درج**: `Payments.SessionId` يعزو النقد لجلسة الدرج الفعلية التي استلمته وقد تختلف عن جلسة الفاتورة (معلّقات/مرتجعات).
- **احتياط المعلّقات**: `Inventory.QuantityReserved` يحجز الكمية للفاتورة المعلّقة حتى تُستأنف أو تُلغى.
- **تحديث تدريجي**: سكربتات `Upgrade_0X` للخوادم المنصوبة مسبقًا دون لمس البيانات.

23 tables in `SaylDB.sql`: parties (customers/suppliers), employees, roles, users, products, batches, inventory, invoices & items, payments, stock movements, expenses, system logs, POS sessions, settings, cash movements, shifts, payroll records, stock counts.

Design highlights: UTC timestamps everywhere with store-localtime display; financial CHECK constraints (positive amounts, non-negative net for sales/purchases, non-negative party balances); unique indexes (product barcode, party email, one user per employee, one open drawer session per user, batch per product+number); drawer-session routing on payments (`Payments.SessionId`); quantity reservation for suspended invoices; incremental `Upgrade_0X` scripts for existing servers.

---

## الترخيص / Licensing

- **PosLicensing**: محرك ترخيص بلا اتصال مدمج في التطبيق — بصمة جهاز (HWID) من CPU/Disk/Board/BIOS، رخصة = `base64(payload).base64(signature)` بتوقيع RSA-2048، تحقق من انتهاء الصلاحية وربط الجهاز أصلًا.
- **مرساة زمن**: `LastSeenUtc` في التسجيل يرفض أي رجوع للساعة يتجاوز اليوم — حماية من تهريب الترخيص بالتلاعب بالوقت.
- **KeygenTool**: أداة الموزّع فقط (لا تُشحن مع المنتج) — توليد المفاتيح وإصدار رخص لكل جهاز.
- **تفعيل**: شاشة `ActivationForm` قبل الدخول؛ الرخصة محفوظة مشفّرة DPAPI.

Online-less licensing: per-device fingerprint (HWID) from CPU/Disk/Board/BIOS; a license is `base64(payload).base64(signature)` signed with RSA-2048, expiry and hardware binding enforced; clock-rollback protection via a `LastSeenUtc` registry anchor; `KeygenTool` is vendor-only (never shipped); activation happens in `ActivationForm` before login, license stored DPAPI-encrypted.

---

## التثبيت والانتشار / Installation & Deployment

المثبّت `installer\setup.iss` (Inno Setup) يحزّم كل شيء:

- **دور رئيسي (MAIN)**: تثبيت صامت لـ SQL Server 2022 Express (مثبّت محليًا في الحزمة، ~250MB) بإنشاء `SAYL` وTCP 1433، إنشاء تسجيل دخول `sayl` بأدنى امتياز (dbcreator + db_backupoperator + dbo — لا sysadmin أبدًا)، تهيئة القاعدة من سكربتات `SaylDB.sql` + بيانات تجريبية اختيارية، كتابة `appsettings.json`.
- **دور شاشة (TERMINAL)**: الاتصال بالجهاز الرئيسي عبر الشبكة المحلية باسم الكمبيوتر + رمز وصول مولّد.
- **بدون كلمات مرور افتراضية**: صفحة «إنشاء حساب المدير» في المعالج تجمع اسم المستخدم وكلمة المرور، ويُنشئ الحساب عند أول تشغيل؛ والتركيب الصامت يعرض الشاشة التفاعلية عند أول إقلاع.
- **تحقق آلي**: `installer\build-installer.ps1` ينشر التطبيق، يجهّز سكربتات القاعدة، يوضع الـ redist، ويجمّع بـ ISCC في حزمة واحدة.

The installer (`setup.iss`, Inno Setup) ships as one package: **MAIN role** silently installs SQL Server 2022 Express (bundled offline, ~250 MB) on instance `SAYL` (TCP 1433), creates the least-privilege `sayl` login (dbcreator + db_backupoperator + dbo — never sysadmin), builds the database from `SaylDB.sql` with optional demo data, and writes `appsettings.json`; **TERMINAL role** joins the main PC over LAN with a generated access code; **no default credentials** — the wizard collects the admin account (created on first launch, or interactively for silent installs). `build-installer.ps1` automates publish, staging, and ISCC compilation.

---

## الأمان / Security

- **لا كلمات مرور افتراضية**: أُزيلت حسابات `admin/Admin@123` و`cashier1` المزروعة؛ حساب المدير يُنشأ بمعرفة الواضبة فقط.
- **أدنى امتياز لقول السايبول**: تسجيل `sayl` لا يملك sysadmin بل الأدوار اللازمة فقط.
- **كلمات مرور بأسلوب BCrypt** مع حساب مضاد للتوقيت (dummy hash) لغير الموجودين، وإخفاء أخطاء الدخول الموحدة.
- **سجل تدقيق** (SystemLogs) لعمليات الحساسة (بيع، مرجعات، رواتب، مستخدمون)، best-effort ولا يعطّل العمل عند امتلائه.
- **حراس مالية في القاعدة والخدمة**: قيود CHECK + تحقق قبل كل معاملة.
- رحلة تحصين موثقة: تقرير هجوم بمدد 45 مكتشفًا، أُغلق 44/45 مختبرًا حيًا (VULN-22 مؤجل بقرار المالك لتدوير مفتاح الاختبار قبل الإطلاق)، إضافة VULN-46/47 بعد التقرير.

- **No default passwords**: seeded `admin/Admin@123` and `cashier1` removed; the admin account is created by the owner.
- **Least privilege for SQL**: the `sayl` login is never sysadmin.
- **BCrypt passwords** with timing-safe dummy hashes and unified error messages.
- **Audit log** (SystemLogs) for sensitive operations, best-effort (never blocks work).
- **Financial guards at DB and service level**: CHECK constraints plus per-transaction validation.
- Hardening is documented: a 45-finding attack report, 44/45 closed and live-tested (VULN-22 deferred by owner decision to rotate the test key pre-launch), plus post-report VULN-46/47.

---

## الشروع في العمل / Getting Started

**المتطلبات**: Windows 10/11 · .NET 10 SDK · SQL Server Express (أو استخدام المثبّت المرفق). Inno Setup 6 للتجميع.

```bash
# البناء
dotnet build Sayl.UI\Sayl.UI.csproj

# تشغيل الواجهة
dotnet run --project Sayl.UI\Sayl.UI.csproj

# اختبار E2E على قاعدة حية
dotnet run --project "consol app\consol app.csproj"

# بناء الحزمة الكاملة
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.0.0
```

**Requirements**: Windows 10/11 · .NET 10 SDK · SQL Server Express (or use the bundled installer). Inno Setup 6 to compile the package.

```bash
# Build
dotnet build Sayl.UI\Sayl.UI.csproj

# Run the UI
dotnet run --project Sayl.UI\Sayl.UI.csproj

# E2E checks against a live database
dotnet run --project "consol app\consol app.csproj"

# Full installer package
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.0.0
```

**الملاحظة**: الهارنس يتطلب قاعدة SQL حية قابلة للاتصال (بصمت اتصال التطبيق) — لا يفتح اتصالات وهمية.

**Note**: the E2E harness requires a reachable live SQL database (it uses the app's connection) — no mocks.

---

## هيكل المستودع / Repository Layout

```
Sayl_POS/
├─ Sayl.UI/            الواجهة (WinForms) — الشاشات والوحدة والموارد والترجمات
├─ Sayl_DataAccess/    Sayl.Core — النماذج/السياق (EF Core) وخدمات الأعمال
├─ Common/             الأنواع المشتركة: DTO وResult والصلاحيات والثوابت
├─ DependencyInjection/ مركّب الحقن: تسجيل القاعدة + الخدمات
├─ PosLicensing/       محرك الترخيص المدمج
├─ KeygenTool/         أداة الموزّع لإصدار الرخص (لا تُشحن)
├─ installer/          Inno Setup + build-installer.ps1 + SQL redist
├─ consol app/         هارنس الاختبار المتكامل (E2E)
├─ Services/           كود استقرار قديم (غير مربوط بالحقن)
└─ docs/               خطط وتوثيق (ترحيل بيانات، إلخ.)
```

---

## التوثيق / Documentation

| الملف                        | File                         | المحتوى                        |
|------------------------------|------------------------------|--------------------------------|
| `Agent.md`                   | Agents context               | هندسة المشروع واتفاقيات الكود  |
| `TASK.md`                    | Backlog                      | المهمات القادمة والنوايا       |
| `ATTACK_REPORT.md`           | Attack report                | مراجعة الهجوم (45 مكتشفًا)     |
| `REMEDIATION_CHECKLIST.md`   | Remediation log              | سجل إصلاح المكتشفات P0..P3     |
| `docs/MIGRATION_PLAN.md`     | Migration plan               | استيراد من Excel/CSV مستقبلًا  |
| `LicenseSystem/LICENSE-SYSTEM-SPEC.md` | Licensing spec   | مواصفة الترخيص والتفعيل        |

---

**سيل — نظام نقاط بيع يبقى في يد صاحبه.** — Sayl POS keeps the business in the merchant's hands.