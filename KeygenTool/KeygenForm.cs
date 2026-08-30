//using System;
//using System.Diagnostics;
//using System.Drawing;
//using System.IO;
//using System.Security.Cryptography;
//using System.Text;
//using System.Windows.Forms;

//namespace PosLicensing.KeygenTool
//{
//    /// <summary>
//    /// أداة الموزّع لإنشاء رخص سيل: توليد زوج المفاتيح مرة واحدة، ثم إصدار
//    /// رخصة موقّعة مربوطة بكود جهاز العميل. واجهة رسومية ثنائية اللغة (عربي/إنجليزي).
//    /// </summary>
//    public class KeygenForm : Form
//    {
//        private const string PrivateKeyFile = "private_key.xml";
//        private const string PublicKeyFile = "public_key.xml";

//        private static string KeyDir => AppDomain.CurrentDomain.BaseDirectory;
//        private static string PrivateKeyPath => Path.Combine(KeyDir, PrivateKeyFile);
//        private static string PublicKeyPath => Path.Combine(KeyDir, PublicKeyFile);

//        private readonly string _instructionsText = string.Join(Environment.NewLine,
//            "١) انسخ محتوى public_key.xml بالكامل إلى ActivationManager.PublicKeyXml داخل مشروع الـ POS ثم أعد بناء البرنامج.",
//            "   Paste public_key.xml into ActivationManager.PublicKeyXml in the POS project, then rebuild.",
//            "",
//            "٢) احفظ private_key.xml في مكان آمن ومشفَّر خارج هذا الجهاز — ضياعه أو تسريبه يعني كسر نظام الترخيص كاملاً.",
//            "   Keep private_key.xml safe and encrypted off-machine; losing or leaking it breaks the whole system.",
//            "",
//            "٣) لا تُلحق ملفي المفاتيح ولا هذه الأداة بأي حزمة توزيع للعملاء.",
//            "   Never ship key files or this tool inside any customer distribution package.");

//        private TabControl _tabs;
//        private TextBox _txtDevice;
//        private Label _lblDeviceState;
//        private TextBox _txtCustomer;
//        private CheckBox _chkPermanent;
//        private NumericUpDown _nudDays;
//        private Label _lblExpiryPreview;
//        private Button _btnGenerate;
//        private TextBox _txtLicenseOut;
//        private Button _btnCopy;
//        private Label _lblSummary;
//        private Label _lblStatus;
//        private Label _lblPrivState;
//        private Label _lblPubState;

//        public KeygenForm()
//        {
//            Text = "أداة توليد الرخص — Sayl License Tool";
//            StartPosition = FormStartPosition.CenterScreen;
//            ClientSize = new Size(700, 664);
//            MinimumSize = new Size(640, 600);
//            BackColor = Theme.Bg;
//            RightToLeft = RightToLeft.Yes;
//            Font = Theme.FontBody;
//            Icon = SystemIcons.Shield;

//            var header = BuildHeader();
//            _tabs = BuildTabs();
//            var footer = BuildFooter();

//            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 6) };
//            host.Controls.Add(_tabs);

//            Controls.Add(footer);
//            Controls.Add(header);
//            Controls.Add(host);
//            host.BringToFront();

//            RefreshKeyStatus();
//            UpdateDeviceState();
//            UpdateExpiryPreview();
//        }

//        // ── الترويسة ──
//        private Control BuildHeader()
//        {
//            var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Primary };
//            var title = new Label
//            {
//                Text = "أداة توليد الرخص",
//                ForeColor = Color.White,
//                Font = Theme.FontTitle,
//                AutoSize = true,
//                BackColor = Color.Transparent,
//                Location = new Point(24, 12)
//            };
//            var sub = new Label
//            {
//                Text = "Sayl License Tool — خاصة بالموزّع فقط / Vendor-only",
//                ForeColor = Theme.PrimaryLight,
//                Font = Theme.FontSmall,
//                AutoSize = true,
//                BackColor = Color.Transparent,
//                Location = new Point(24, 52)
//            };
//            void LayoutHeader()
//            {
//                int x = header.Width - Math.Max(title.Width, sub.Width) - 24;
//                title.Left = x;
//                sub.Left = x;
//            }
//            header.Resize += (_, _) => LayoutHeader();
//            header.Controls.Add(title);
//            header.Controls.Add(sub);
//            return header;
//        }

//        // ── التبويبات ──
//        private TabControl BuildTabs()
//        {
//            var tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.FontBody };
//            tabs.TabPages.Add(BuildCreateTab());
//            tabs.TabPages.Add(BuildKeysTab());
//            return tabs;
//        }

//        private TabPage BuildCreateTab()
//        {
//            var page = new TabPage("إنشاء رخصة / Create License")
//            {
//                BackColor = Theme.CardBg,
//                Padding = new Padding(14)
//            };

//            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
//            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));    // عنوان كود الجهاز
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));    // حقل الكود
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));    // حالة الكود
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // عنوان الاسم
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));    // حقل الاسم
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // عنوان المدة
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));    // صف المدة
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));    // معاينة الانتهاء
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));    // زر الإنشاء
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // عنوان الناتج
//            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // صندوق الناتج
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));    // الملخص
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));    // زر النسخ

//            root.Controls.Add(MakeSectionLabel("كود الجهاز / Device Code"));
//            root.Controls.Add(Host(BuildDeviceRow(), 46));
//            _lblDeviceState = MakeHintLabel("");
//            root.Controls.Add(_lblDeviceState);

//            root.Controls.Add(MakeSectionLabel("اسم العميل / Customer Name"));
//            _txtCustomer = Theme.MakeField();
//            root.Controls.Add(HostStretched(_txtCustomer, 44));

//            root.Controls.Add(MakeSectionLabel("الصلاحية / Duration"));
//            root.Controls.Add(Host(BuildDurationRow(), 46));
//            _lblExpiryPreview = MakeHintLabel("");
//            root.Controls.Add(_lblExpiryPreview);

//            _btnGenerate = Theme.MakeButton("إنشاء الرخصة / Generate", Theme.Primary, height: 46);
//            _btnGenerate.Click += (_, _) => GenerateLicense();
//            root.Controls.Add(HostStretched(_btnGenerate, 46));

//            root.Controls.Add(MakeSectionLabel("الرخصة — انسخها وأرسلها للعميل / License"));
//            _txtLicenseOut = new TextBox
//            {
//                Multiline = true,
//                ReadOnly = true,
//                ScrollBars = ScrollBars.Vertical,
//                BackColor = Theme.FieldBg,
//                BorderStyle = BorderStyle.FixedSingle,
//                Font = Theme.FontMono,
//                RightToLeft = RightToLeft.No,
//                WordWrap = false,
//                Dock = DockStyle.Fill
//            };
//            root.Controls.Add(_txtLicenseOut);

//            _lblSummary = MakeHintLabel("");
//            root.Controls.Add(_lblSummary);

//            _btnCopy = Theme.MakeButton("نسخ الرخصة / Copy License", Theme.PrimaryLight,
//                foreColor: Theme.PrimaryDark, hoverColor: Color.FromArgb(200, 216, 233), height: 44);
//            _btnCopy.Enabled = false;
//            _btnCopy.Click += (_, _) => CopyLicense();

//            var actionsRow = new FlowLayoutPanel
//            {
//                Dock = DockStyle.Fill,
//                FlowDirection = FlowDirection.RightToLeft,
//                WrapContents = false,
//                Margin = new Padding(0),
//                Padding = new Padding(24, 0, 24, 0)
//            };

//            // زر النسخ (الموجود)
//            _btnCopy.Width = 220;
//            _btnCopy.Height = 44;

//            // زر حفظ الرخصة
//            var btnSave = Theme.MakeButton("حفظ كملف .lic", Theme.Success,
//                foreColor: Color.White, hoverColor: Theme.Success, height: 44);
//            btnSave.Width = 180;
//            btnSave.Enabled = false;
//            btnSave.Click += (_, _) => SaveLicenseFile();

//            // حفظ مرجع الزر إذا أردت تمكينه مع _btnCopy
//            // نضيف متغير عام `_btnSave` في الكلاس، وإلا يمكن تمكينه داخل دالة توليد الرخصة.
//            // في هذا المثال سنستخدم متغير عام.

//            // أضف الزرين إلى الحاوية
//            actionsRow.Controls.Add(_btnCopy);
//            actionsRow.Controls.Add(btnSave);

//            // استبدل `root.Controls.Add(HostStretched(_btnCopy, 44));` بـ:
//            root.Controls.Add(actionsRow);
//            //root.Controls.Add(HostStretched(_btnCopy, 44));

//            page.Controls.Add(root);
//            return page;
//        }

//        private Control BuildDeviceRow()
//        {
//            var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
//            _txtDevice = new TextBox
//            {
//                Font = Theme.FontMonoBig,
//                BackColor = Theme.FieldBg,
//                BorderStyle = BorderStyle.FixedSingle,
//                RightToLeft = RightToLeft.No,
//                TextAlign = HorizontalAlignment.Center,
//                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
//            };
//            _txtDevice.TextChanged += (_, _) => OnDeviceTextChanged();

//            void LayoutRow()
//            {
//                int w = Math.Max(panel.Width - 24 * 2, 200);
//                _txtDevice.SetBounds(24, 2, w, 38);
//            }
//            panel.Resize += (_, _) => LayoutRow();
//            panel.HandleCreated += (_, _) => LayoutRow();
//            panel.Controls.Add(_txtDevice);
//            return panel;
//        }

//        private Control BuildDurationRow()
//        {
//            var flow = new FlowLayoutPanel
//            {
//                Dock = DockStyle.Fill,
//                FlowDirection = FlowDirection.RightToLeft,
//                WrapContents = false,
//                Margin = new Padding(0)
//            };

//            var daysLabel = new Label
//            {
//                Text = "عدد الأيام:",
//                AutoSize = true,
//                Font = Theme.FontBody,
//                Margin = new Padding(8, 13, 4, 0)
//            };

//            _nudDays = new NumericUpDown
//            {
//                Minimum = 1,
//                Maximum = 3650,
//                Value = 365,
//                Width = 90,
//                Font = Theme.FontBody,
//                Margin = new Padding(2, 9, 2, 0)
//            };
//            _nudDays.ValueChanged += (_, _) => UpdateExpiryPreview();

//            _chkPermanent = new CheckBox
//            {
//                Text = "رخصة دائمة / Permanent",
//                AutoSize = true,
//                Font = Theme.FontBody,
//                Checked = false,
//                Margin = new Padding(8, 11, 8, 0)
//            };
//            _chkPermanent.CheckedChanged += (_, _) =>
//            {
//                _nudDays.Enabled = !_chkPermanent.Checked;
//                UpdateExpiryPreview();
//            };

//            flow.Controls.Add(daysLabel);
//            flow.Controls.Add(_nudDays);
//            flow.Controls.Add(_chkPermanent);
//            return flow;
//        }

//        private TabPage BuildKeysTab()
//        {
//            var page = new TabPage("المفاتيح / Keys")
//            {
//                BackColor = Theme.CardBg,
//                Padding = new Padding(14)
//            };

//            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
//            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // عنوان الحالة
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));    // حالة الخاص
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));    // حالة العام
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));    // المسار
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));    // الأزرار
//            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // عنوان التعليمات
//            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // التعليمات

//            root.Controls.Add(MakeSectionLabel("حالة المفاتيح / Key Files"));

//            _lblPrivState = MakeHintLabel("");
//            _lblPubState = MakeHintLabel("");
//            root.Controls.Add(_lblPrivState);
//            root.Controls.Add(_lblPubState);
//            root.Controls.Add(MakeHintLabel("المجلد / Folder:  " + KeyDir));

//            var btnRow = new FlowLayoutPanel
//            {
//                Dock = DockStyle.Fill,
//                FlowDirection = FlowDirection.RightToLeft,
//                WrapContents = false,
//                Margin = new Padding(0)
//            };
//            var btnGenerateKeys = Theme.MakeButton("توليد زوج مفاتيح جديد / New Key Pair", Theme.Warning, height: 46);
//            btnGenerateKeys.Width = 320;
//            btnGenerateKeys.Click += (_, _) => GenerateKeyPair();
//            var btnOpenFolder = Theme.MakeButton("فتح المجلد / Open Folder", Theme.PrimaryLight,
//                foreColor: Theme.PrimaryDark, hoverColor: Color.FromArgb(200, 216, 233), height: 46);
//            btnOpenFolder.Width = 190;
//            btnOpenFolder.Click += (_, _) => OpenKeyFolder();
//            btnRow.Controls.Add(btnGenerateKeys);
//            btnRow.Controls.Add(btnOpenFolder);
//            root.Controls.Add(btnRow);

//            root.Controls.Add(MakeSectionLabel("خطوات مهمة / Important Steps"));

//            var instructions = new TextBox
//            {
//                Multiline = true,
//                ReadOnly = true,
//                ScrollBars = ScrollBars.Vertical,
//                BackColor = Theme.FieldBg,
//                BorderStyle = BorderStyle.FixedSingle,
//                Font = Theme.FontBody,
//                Dock = DockStyle.Fill,
//                Text = _instructionsText
//            };
//            root.Controls.Add(instructions);

//            page.Controls.Add(root);
//            return page;
//        }

//        // ── التذييل ──
//        private Control BuildFooter()
//        {
//            var footer = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Theme.CardBg };
//            _lblStatus = new Label
//            {
//                Dock = DockStyle.Fill,
//                TextAlign = ContentAlignment.MiddleLeft,
//                Font = Theme.FontSmall,
//                Padding = new Padding(12, 0, 12, 0)
//            };
//            footer.Controls.Add(_lblStatus);
//            return footer;
//        }

//        // ── مساعدات تخطيط ──
//        private static Label MakeSectionLabel(string text)
//        {
//            return new Label
//            {
//                Text = text,
//                AutoSize = true,
//                Font = Theme.FontHeading,
//                ForeColor = Theme.PrimaryDark,
//                Margin = new Padding(4, 6, 4, 2)
//            };
//        }

//        private static Label MakeHintLabel(string text)
//        {
//            return new Label
//            {
//                Text = text,
//                AutoSize = true,
//                Font = Theme.FontSmall,
//                ForeColor = Theme.TextMuted,
//                Margin = new Padding(26, 2, 26, 0)
//            };
//        }

//        /// <summary>حاوية بحشو جانبي، العنصر الداخلي يبقى بحجمه الطبيعي.</summary>
//        private static Panel Host(Control inner, int height)
//        {
//            var p = new Panel { Dock = DockStyle.Fill, Height = height, Margin = new Padding(0) };
//            p.Controls.Add(inner);
//            return p;
//        }

//        /// <summary>حاوية بحشو جانبي، العنصر الداخلي يتمدد لعرض الحاوية.</summary>
//        private static Panel HostStretched(Control inner, int height)
//        {
//            var p = new Panel { Dock = DockStyle.Fill, Height = height, Margin = new Padding(0) };
//            inner.Dock = DockStyle.None;
//            void Layout()
//            {
//                inner.SetBounds(24, 2, Math.Max(p.Width - 48, 80), height - 6);
//            }
//            p.Resize += (_, _) => Layout();
//            p.HandleCreated += (_, _) => Layout();
//            p.Controls.Add(inner);
//            return p;
//        }

//        // ── منطق كود الجهاز ──
//        private static string FormatDeviceCode(string raw)
//        {
//            var sb = new StringBuilder();
//            foreach (char ch in raw.ToUpperInvariant())
//            {
//                if (Uri.IsHexDigit(ch))
//                {
//                    sb.Append(ch);
//                    if (sb.Length == 16) break;
//                }
//            }
//            for (int i = 4; i < sb.Length; i += 5)
//                sb.Insert(i, '-');
//            return sb.ToString();
//        }

//        private void OnDeviceTextChanged()
//        {
//            string formatted = FormatDeviceCode(_txtDevice.Text);
//            if (_txtDevice.Text != formatted)
//            {
//                int caretFromEnd = _txtDevice.TextLength - _txtDevice.SelectionStart;
//                _txtDevice.Text = formatted;
//                _txtDevice.SelectionStart = Math.Max(formatted.Length - caretFromEnd, 0);
//            }
//            UpdateDeviceState();
//        }

//        private void UpdateDeviceState()
//        {
//            string norm = MachineFingerprint.Normalize(_txtDevice.Text);
//            bool valid = norm.Length == 16;
//            if (_txtDevice.TextLength == 0)
//            {
//                _lblDeviceState.Text = "الصق كود الجهاز الذي أرسله العميل / Paste the device code sent by the customer";
//                _lblDeviceState.ForeColor = Theme.TextMuted;
//            }
//            else if (valid)
//            {
//                _lblDeviceState.Text = "الكود صالح / Valid device code";
//                _lblDeviceState.ForeColor = Theme.Success;
//            }
//            else
//            {
//                _lblDeviceState.Text = "غير مكتمل — يجب 16 خانة ست عشرية / Incomplete — needs 16 hex characters";
//                _lblDeviceState.ForeColor = Theme.Danger;
//            }
//            _btnGenerate.Enabled = valid;
//        }

//        // ── منطق الصلاحية ──
//        private void UpdateExpiryPreview()
//        {
//            if (_chkPermanent.Checked)
//            {
//                _lblExpiryPreview.Text = "تنتهي عند: دائمة — لا تنتهي أبداً / Expires: never";
//            }
//            else
//            {
//                DateTime end = DateTime.UtcNow.AddDays((double)_nudDays.Value).ToLocalTime();
//                _lblExpiryPreview.Text = "تنتهي عند: " + end.ToString("yyyy/MM/dd HH:mm") +
//                    "   (بعد " + _nudDays.Value + " يوم)";
//            }
//            _lblExpiryPreview.ForeColor = Theme.TextMuted;
//        }

//        // ── إنشاء الرخصة ──
//        private void GenerateLicense()
//        {
//            string hwid = MachineFingerprint.Normalize(_txtDevice.Text);
//            if (hwid.Length != 16)
//            {
//                ShowError("كود الجهاز غير مكتمل (يجب 16 خانة بعد حذف الشرطات).\r\nIncomplete device code (needs 16 hex characters).");
//                return;
//            }

//            if (!File.Exists(PrivateKeyPath))
//            {
//                ShowError("لم يتم العثور على private_key.xml بجانب الأداة.\r\nولّد زوج المفاتيح أولاً من تبويب «المفاتيح».\r\n\r\n" +
//                    "private_key.xml was not found next to the tool.\r\nGenerate a key pair first from the Keys tab.");
//                return;
//            }

//            try
//            {
//                int days = _chkPermanent.Checked ? 0 : (int)_nudDays.Value;
//                long expiry = days <= 0
//                    ? DateTime.MaxValue.Ticks
//                    : DateTime.UtcNow.AddDays(days).Ticks;

//                var payload = new LicensePayload
//                {
//                    Hwid = hwid,
//                    ExpiryTicksUtc = expiry,
//                    Customer = _txtCustomer.Text ?? ""
//                };

//                string license = LicenseCrypto.Sign(payload, File.ReadAllText(PrivateKeyPath));
//                _txtLicenseOut.Text = license;
//                _btnCopy.Enabled = true;

//                string expiryLine = days <= 0
//                    ? "دائمة / Permanent"
//                    : DateTime.UtcNow.AddDays(days).ToLocalTime().ToString("yyyy/MM/dd");

//                string customer = string.IsNullOrWhiteSpace(payload.Customer)
//                    ? "—" : payload.Customer.Trim();

//                _lblSummary.Text =
//                    "العميل: " + customer +
//                    "   |   الكود: " + FormatDeviceCode(hwid) +
//                    "   |   الصلاحية: " + expiryLine;
//                _lblSummary.ForeColor = Theme.Success;
//            }
//            catch (Exception ex)
//            {
//                ShowError("تعذر إنشاء الرخصة:\r\n" + ex.Message);
//            }
//        }

//        // ── النسخ ──
//        private void CopyLicense()
//        {
//            if (_txtLicenseOut.TextLength == 0) return;
//            Clipboard.SetText(_txtLicenseOut.Text);
//            string original = _btnCopy.Text;
//            _btnCopy.Text = "تم النسخ / Copied!";
//            var t = new Timer { Interval = 1200 };
//            t.Tick += (_, _) =>
//            {
//                _btnCopy.Text = original;
//                t.Stop();
//                t.Dispose();
//            };
//            t.Start();
//        }

//        // ── إدارة المفاتيح ──
//        private void GenerateKeyPair()
//        {
//            if (File.Exists(PrivateKeyPath))
//            {
//                var answer = MessageBox.Show(this,
//                    "تحذير: private_key.xml موجود بالفعل!\r\n" +
//                    "إعادة التوليد تجعل كل الرخص الصادرة سابقاً باطلة تماماً.\r\n\r\n" +
//                    "هل أنت متأكد؟\r\n\r\n" +
//                    "Warning: private_key.xml already exists!\r\n" +
//                    "Regenerating permanently invalidates every previously issued license.\r\n\r\n" +
//                    "Are you sure?",
//                    "تأكيد إعادة التوليد / Confirm Regeneration",
//                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
//                    MessageBoxDefaultButton.Button2, RtlOptions());
//                if (answer != DialogResult.Yes) return;
//            }

//            try
//            {
//                using (RSA rsa = RSA.Create(2048))
//                {
//                    File.WriteAllText(PrivateKeyPath, rsa.ToXmlString(true));
//                    File.WriteAllText(PublicKeyPath, rsa.ToXmlString(false));
//                }
//                RefreshKeyStatus();
//                _tabs.SelectedIndex = 1;
//                MessageBox.Show(this,
//                    "تم إنشاء المفتاحين بنجاح في:\r\n" + KeyDir + "\r\n\r\n" +
//                    "لا تنسَ نسخ public_key.xml إلى ActivationManager.PublicKeyXml وإعادة بناء برنامج الـ POS.\r\n\r\n" +
//                    "Key pair generated successfully.\r\n" +
//                    "Remember to paste public_key.xml into ActivationManager.PublicKeyXml and rebuild the POS.",
//                    "تم / Done",
//                    MessageBoxButtons.OK, MessageBoxIcon.Information,
//                    MessageBoxDefaultButton.Button1, RtlOptions());
//            }
//            catch (Exception ex)
//            {
//                ShowError("تعذر توليد المفاتيح:\r\n" + ex.Message);
//            }
//        }

//        private void OpenKeyFolder()
//        {
//            try { Process.Start("explorer.exe", "\"" + KeyDir.TrimEnd('\\') + "\""); }
//            catch { /* تجاهل */ }
//        }

//        private void RefreshKeyStatus()
//        {
//            bool priv = File.Exists(PrivateKeyPath);
//            bool pub = File.Exists(PublicKeyPath);

//            _lblPrivState.Text = priv
//                ? "private_key.xml  —  موجود / present  (سرّ مطلق، لا يُوزَّع أبداً / never distribute)"
//                : "private_key.xml  —  غير موجود / missing";
//            _lblPrivState.ForeColor = priv ? Theme.Success : Theme.Danger;

//            _lblPubState.Text = pub
//                ? "public_key.xml  —  موجود / present"
//                : "public_key.xml  —  غير موجود / missing";
//            _lblPubState.ForeColor = pub ? Theme.Success : Theme.Danger;

//            if (_lblStatus != null)
//            {
//                if (priv)
//                {
//                    _lblStatus.Text = "المفاتيح جاهزة — يمكنك إصدار الرخص / Keys ready — you can issue licenses";
//                    _lblStatus.ForeColor = Theme.Success;
//                }
//                else
//                {
//                    _lblStatus.Text = "لا توجد مفاتيح — ولّدها من تبويب «المفاتيح» أولاً / No keys yet — generate them from the Keys tab first";
//                    _lblStatus.ForeColor = Theme.Danger;
//                }
//            }
//        }

//        private void ShowError(string msg)
//        {
//            MessageBox.Show(this, msg, "خطأ / Error",
//                MessageBoxButtons.OK, MessageBoxIcon.Error,
//                MessageBoxDefaultButton.Button1, RtlOptions());
//        }

//        private static MessageBoxOptions RtlOptions()
//        {
//            return MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;
//        }
//    }
//}




using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace PosLicensing.KeygenTool
{
    /// <summary>
    /// أداة الموزّع لإنشاء رخص سيل: توليد زوج المفاتيح مرة واحدة، ثم إصدار
    /// رخصة موقّعة مربوطة بكود جهاز العميل. واجهة رسومية ثنائية اللغة (عربي/إنجليزي).
    /// </summary>
    public class KeygenForm : Form
    {
        private const string PrivateKeyFile = "private_key.xml";
        private const string PublicKeyFile = "public_key.xml";

        private static string KeyDir => AppDomain.CurrentDomain.BaseDirectory;
        private static string PrivateKeyPath => Path.Combine(KeyDir, PrivateKeyFile);
        private static string PublicKeyPath => Path.Combine(KeyDir, PublicKeyFile);

        private readonly string _instructionsText = string.Join(Environment.NewLine,
            "١) انسخ محتوى public_key.xml بالكامل إلى ActivationManager.PublicKeyXml داخل مشروع الـ POS ثم أعد بناء البرنامج.",
            "   Paste public_key.xml into ActivationManager.PublicKeyXml in the POS project, then rebuild.",
            "",
            "٢) احفظ private_key.xml في مكان آمن ومشفَّر خارج هذا الجهاز — ضياعه أو تسريبه يعني كسر نظام الترخيص كاملاً.",
            "   Keep private_key.xml safe and encrypted off-machine; losing or leaking it breaks the whole system.",
            "",
            "٣) لا تُلحق ملفي المفاتيح ولا هذه الأداة بأي حزمة توزيع للعملاء.",
            "   Never ship key files or this tool inside any customer distribution package.");

        private TabControl _tabs;
        private TextBox _txtDevice;
        private Label _lblDeviceState;
        private TextBox _txtCustomer;
        private CheckBox _chkPermanent;
        private NumericUpDown _nudDays;
        private Label _lblExpiryPreview;
        private Button _btnGenerate;
        private TextBox _txtLicenseOut;
        private Button _btnCopy;
        private Button _btnSaveLic;   // ← الحقل الجديد
        private Label _lblSummary;
        private Label _lblStatus;
        private Label _lblPrivState;
        private Label _lblPubState;

        public KeygenForm()
        {
            Text = "أداة توليد الرخص — Sayl License Tool";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(700, 664);
            MinimumSize = new Size(640, 600);
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.Yes;
            Font = Theme.FontBody;
            Icon = SystemIcons.Shield;

            var header = BuildHeader();
            _tabs = BuildTabs();
            var footer = BuildFooter();

            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 6) };
            host.Controls.Add(_tabs);

            Controls.Add(footer);
            Controls.Add(header);
            Controls.Add(host);
            host.BringToFront();

            RefreshKeyStatus();
            UpdateDeviceState();
            UpdateExpiryPreview();
        }

        // ── الترويسة ──
        private Control BuildHeader()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Primary };
            var title = new Label
            {
                Text = "أداة توليد الرخص",
                ForeColor = Color.White,
                Font = Theme.FontTitle,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(24, 12)
            };
            var sub = new Label
            {
                Text = "Sayl License Tool — خاصة بالموزّع فقط / Vendor-only",
                ForeColor = Theme.PrimaryLight,
                Font = Theme.FontSmall,
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(24, 52)
            };
            void LayoutHeader()
            {
                int x = header.Width - Math.Max(title.Width, sub.Width) - 24;
                title.Left = x;
                sub.Left = x;
            }
            header.Resize += (_, _) => LayoutHeader();
            header.Controls.Add(title);
            header.Controls.Add(sub);
            return header;
        }

        // ── التبويبات ──
        private TabControl BuildTabs()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.FontBody };
            tabs.TabPages.Add(BuildCreateTab());
            tabs.TabPages.Add(BuildKeysTab());
            return tabs;
        }

        private TabPage BuildCreateTab()
        {
            var page = new TabPage("إنشاء رخصة / Create License")
            {
                BackColor = Theme.CardBg,
                Padding = new Padding(14)
            };

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

            root.Controls.Add(MakeSectionLabel("كود الجهاز / Device Code"));
            root.Controls.Add(Host(BuildDeviceRow(), 46));
            _lblDeviceState = MakeHintLabel("");
            root.Controls.Add(_lblDeviceState);

            root.Controls.Add(MakeSectionLabel("اسم العميل / Customer Name"));
            _txtCustomer = Theme.MakeField();
            root.Controls.Add(HostStretched(_txtCustomer, 44));

            root.Controls.Add(MakeSectionLabel("الصلاحية / Duration"));
            root.Controls.Add(Host(BuildDurationRow(), 46));
            _lblExpiryPreview = MakeHintLabel("");
            root.Controls.Add(_lblExpiryPreview);

            _btnGenerate = Theme.MakeButton("إنشاء الرخصة / Generate", Theme.Primary, height: 46);
            _btnGenerate.Click += (_, _) => GenerateLicense();
            root.Controls.Add(HostStretched(_btnGenerate, 46));

            root.Controls.Add(MakeSectionLabel("الرخصة — انسخها وأرسلها للعميل / License"));
            _txtLicenseOut = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.FieldBg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.FontMono,
                RightToLeft = RightToLeft.No,
                WordWrap = false,
                Dock = DockStyle.Fill
            };
            root.Controls.Add(_txtLicenseOut);

            _lblSummary = MakeHintLabel("");
            root.Controls.Add(_lblSummary);

            // ── صف الأزرار: نسخ + حفظ ملف .lic ──
            var actionsRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(24, 0, 24, 0)
            };

            _btnCopy = Theme.MakeButton("نسخ الرخصة / Copy License", Theme.PrimaryLight,
                foreColor: Theme.PrimaryDark, hoverColor: Color.FromArgb(200, 216, 233), height: 44);
            _btnCopy.Width = 200;
            _btnCopy.Enabled = false;
            _btnCopy.Click += (_, _) => CopyLicense();

            _btnSaveLic = Theme.MakeButton("حفظ ملف .lic", Theme.Success,
                foreColor: Color.White, hoverColor: Color.FromArgb(30, 130, 80), height: 44);
            _btnSaveLic.Width = 180;
            _btnSaveLic.Enabled = false;
            _btnSaveLic.Click += (_, _) => SaveLicenseFile();

            actionsRow.Controls.Add(_btnCopy);
            actionsRow.Controls.Add(_btnSaveLic);

            root.Controls.Add(actionsRow);

            page.Controls.Add(root);
            return page;
        }

        private Control BuildDeviceRow()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            _txtDevice = new TextBox
            {
                Font = Theme.FontMonoBig,
                BackColor = Theme.FieldBg,
                BorderStyle = BorderStyle.FixedSingle,
                RightToLeft = RightToLeft.No,
                TextAlign = HorizontalAlignment.Center,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };
            _txtDevice.TextChanged += (_, _) => OnDeviceTextChanged();

            void LayoutRow()
            {
                int w = Math.Max(panel.Width - 24 * 2, 200);
                _txtDevice.SetBounds(24, 2, w, 38);
            }
            panel.Resize += (_, _) => LayoutRow();
            panel.HandleCreated += (_, _) => LayoutRow();
            panel.Controls.Add(_txtDevice);
            return panel;
        }

        private Control BuildDurationRow()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };

            var daysLabel = new Label
            {
                Text = "عدد الأيام:",
                AutoSize = true,
                Font = Theme.FontBody,
                Margin = new Padding(8, 13, 4, 0)
            };

            _nudDays = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 3650,
                Value = 365,
                Width = 90,
                Font = Theme.FontBody,
                Margin = new Padding(2, 9, 2, 0)
            };
            _nudDays.ValueChanged += (_, _) => UpdateExpiryPreview();

            _chkPermanent = new CheckBox
            {
                Text = "رخصة دائمة / Permanent",
                AutoSize = true,
                Font = Theme.FontBody,
                Checked = false,
                Margin = new Padding(8, 11, 8, 0)
            };
            _chkPermanent.CheckedChanged += (_, _) =>
            {
                _nudDays.Enabled = !_chkPermanent.Checked;
                UpdateExpiryPreview();
            };

            flow.Controls.Add(daysLabel);
            flow.Controls.Add(_nudDays);
            flow.Controls.Add(_chkPermanent);
            return flow;
        }

        private TabPage BuildKeysTab()
        {
            var page = new TabPage("المفاتيح / Keys")
            {
                BackColor = Theme.CardBg,
                Padding = new Padding(14)
            };

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            root.Controls.Add(MakeSectionLabel("حالة المفاتيح / Key Files"));

            _lblPrivState = MakeHintLabel("");
            _lblPubState = MakeHintLabel("");
            root.Controls.Add(_lblPrivState);
            root.Controls.Add(_lblPubState);
            root.Controls.Add(MakeHintLabel("المجلد / Folder:  " + KeyDir));

            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var btnGenerateKeys = Theme.MakeButton("توليد زوج مفاتيح جديد / New Key Pair", Theme.Warning, height: 46);
            btnGenerateKeys.Width = 320;
            btnGenerateKeys.Click += (_, _) => GenerateKeyPair();
            var btnOpenFolder = Theme.MakeButton("فتح المجلد / Open Folder", Theme.PrimaryLight,
                foreColor: Theme.PrimaryDark, hoverColor: Color.FromArgb(200, 216, 233), height: 46);
            btnOpenFolder.Width = 190;
            btnOpenFolder.Click += (_, _) => OpenKeyFolder();
            btnRow.Controls.Add(btnGenerateKeys);
            btnRow.Controls.Add(btnOpenFolder);
            root.Controls.Add(btnRow);

            root.Controls.Add(MakeSectionLabel("خطوات مهمة / Important Steps"));

            var instructions = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.FieldBg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.FontBody,
                Dock = DockStyle.Fill,
                Text = _instructionsText
            };
            root.Controls.Add(instructions);

            page.Controls.Add(root);
            return page;
        }

        private Control BuildFooter()
        {
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Theme.CardBg };
            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = Theme.FontSmall,
                Padding = new Padding(12, 0, 12, 0)
            };
            footer.Controls.Add(_lblStatus);
            return footer;
        }

        // ── مساعدات تخطيط ──
        private static Label MakeSectionLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = Theme.FontHeading,
                ForeColor = Theme.PrimaryDark,
                Margin = new Padding(4, 6, 4, 2)
            };
        }

        private static Label MakeHintLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = Theme.FontSmall,
                ForeColor = Theme.TextMuted,
                Margin = new Padding(26, 2, 26, 0)
            };
        }

        private static Panel Host(Control inner, int height)
        {
            var p = new Panel { Dock = DockStyle.Fill, Height = height, Margin = new Padding(0) };
            p.Controls.Add(inner);
            return p;
        }

        private static Panel HostStretched(Control inner, int height)
        {
            var p = new Panel { Dock = DockStyle.Fill, Height = height, Margin = new Padding(0) };
            inner.Dock = DockStyle.None;
            void Layout()
            {
                inner.SetBounds(24, 2, Math.Max(p.Width - 48, 80), height - 6);
            }
            p.Resize += (_, _) => Layout();
            p.HandleCreated += (_, _) => Layout();
            p.Controls.Add(inner);
            return p;
        }

        // ── منطق كود الجهاز ──
        private static string FormatDeviceCode(string raw)
        {
            var sb = new StringBuilder();
            foreach (char ch in raw.ToUpperInvariant())
            {
                if (Uri.IsHexDigit(ch))
                {
                    sb.Append(ch);
                    if (sb.Length == 16) break;
                }
            }
            for (int i = 4; i < sb.Length; i += 5)
                sb.Insert(i, '-');
            return sb.ToString();
        }

        private void OnDeviceTextChanged()
        {
            string formatted = FormatDeviceCode(_txtDevice.Text);
            if (_txtDevice.Text != formatted)
            {
                int caretFromEnd = _txtDevice.TextLength - _txtDevice.SelectionStart;
                _txtDevice.Text = formatted;
                _txtDevice.SelectionStart = Math.Max(formatted.Length - caretFromEnd, 0);
            }
            UpdateDeviceState();
        }

        private void UpdateDeviceState()
        {
            string norm = MachineFingerprint.Normalize(_txtDevice.Text);
            bool valid = norm.Length == 16;
            if (_txtDevice.TextLength == 0)
            {
                _lblDeviceState.Text = "الصق كود الجهاز الذي أرسله العميل / Paste the device code sent by the customer";
                _lblDeviceState.ForeColor = Theme.TextMuted;
            }
            else if (valid)
            {
                _lblDeviceState.Text = "الكود صالح / Valid device code";
                _lblDeviceState.ForeColor = Theme.Success;
            }
            else
            {
                _lblDeviceState.Text = "غير مكتمل — يجب 16 خانة ست عشرية / Incomplete — needs 16 hex characters";
                _lblDeviceState.ForeColor = Theme.Danger;
            }
            _btnGenerate.Enabled = valid;
        }

        private void UpdateExpiryPreview()
        {
            if (_chkPermanent.Checked)
            {
                _lblExpiryPreview.Text = "تنتهي عند: دائمة — لا تنتهي أبداً / Expires: never";
            }
            else
            {
                DateTime end = DateTime.UtcNow.AddDays((double)_nudDays.Value).ToLocalTime();
                _lblExpiryPreview.Text = "تنتهي عند: " + end.ToString("yyyy/MM/dd HH:mm") +
                    "   (بعد " + _nudDays.Value + " يوم)";
            }
            _lblExpiryPreview.ForeColor = Theme.TextMuted;
        }

        private void GenerateLicense()
        {
            string hwid = MachineFingerprint.Normalize(_txtDevice.Text);
            if (hwid.Length != 16)
            {
                ShowError("كود الجهاز غير مكتمل (يجب 16 خانة بعد حذف الشرطات).\r\nIncomplete device code (needs 16 hex characters).");
                return;
            }

            if (!File.Exists(PrivateKeyPath))
            {
                ShowError("لم يتم العثور على private_key.xml بجانب الأداة.\r\nولّد زوج المفاتيح أولاً من تبويب «المفاتيح».\r\n\r\n" +
                    "private_key.xml was not found next to the tool.\r\nGenerate a key pair first from the Keys tab.");
                return;
            }

            try
            {
                int days = _chkPermanent.Checked ? 0 : (int)_nudDays.Value;
                long expiry = days <= 0
                    ? DateTime.MaxValue.Ticks
                    : DateTime.UtcNow.AddDays(days).Ticks;

                var payload = new LicensePayload
                {
                    Hwid = hwid,
                    ExpiryTicksUtc = expiry,
                    Customer = _txtCustomer.Text ?? ""
                };

                string license = LicenseCrypto.Sign(payload, File.ReadAllText(PrivateKeyPath));
                _txtLicenseOut.Text = license;
                _btnCopy.Enabled = true;
                _btnSaveLic.Enabled = true;   // ← تمكين زر حفظ الملف

                string expiryLine = days <= 0
                    ? "دائمة / Permanent"
                    : DateTime.UtcNow.AddDays(days).ToLocalTime().ToString("yyyy/MM/dd");

                string customer = string.IsNullOrWhiteSpace(payload.Customer)
                    ? "—" : payload.Customer.Trim();

                _lblSummary.Text =
                    "العميل: " + customer +
                    "   |   الكود: " + FormatDeviceCode(hwid) +
                    "   |   الصلاحية: " + expiryLine;
                _lblSummary.ForeColor = Theme.Success;
            }
            catch (Exception ex)
            {
                ShowError("تعذر إنشاء الرخصة:\r\n" + ex.Message);
            }
        }

        private void CopyLicense()
        {
            if (_txtLicenseOut.TextLength == 0) return;
            Clipboard.SetText(_txtLicenseOut.Text);
            string original = _btnCopy.Text;
            _btnCopy.Text = "تم النسخ / Copied!";
            var t = new Timer { Interval = 1200 };
            t.Tick += (_, _) =>
            {
                _btnCopy.Text = original;
                t.Stop();
                t.Dispose();
            };
            t.Start();
        }

        // ── دالة حفظ ملف .lic ──
        private void SaveLicenseFile()
        {
            if (string.IsNullOrWhiteSpace(_txtLicenseOut.Text))
                return;

            using (var dlg = new SaveFileDialog
            {
                Filter = "License files (*.lic)|*.lic|All files (*.*)|*.*",
                FileName = "Sayl.lic",
                Title = "حفظ ملف الترخيص"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllText(dlg.FileName, _txtLicenseOut.Text);
                        MessageBox.Show(this,
                            "تم حفظ ملف الترخيص بنجاح.",
                            "تم / Saved",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information,
                            MessageBoxDefaultButton.Button1,
                            RtlOptions());
                    }
                    catch (Exception ex)
                    {
                        ShowError("تعذر حفظ الملف:\r\n" + ex.Message);
                    }
                }
            }
        }

        private void GenerateKeyPair()
        {
            if (File.Exists(PrivateKeyPath))
            {
                var answer = MessageBox.Show(this,
                    "تحذير: private_key.xml موجود بالفعل!\r\n" +
                    "إعادة التوليد تجعل كل الرخص الصادرة سابقاً باطلة تماماً.\r\n\r\n" +
                    "هل أنت متأكد؟\r\n\r\n" +
                    "Warning: private_key.xml already exists!\r\n" +
                    "Regenerating permanently invalidates every previously issued license.\r\n\r\n" +
                    "Are you sure?",
                    "تأكيد إعادة التوليد / Confirm Regeneration",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2, RtlOptions());
                if (answer != DialogResult.Yes) return;
            }

            try
            {
                using (RSA rsa = RSA.Create(2048))
                {
                    File.WriteAllText(PrivateKeyPath, rsa.ToXmlString(true));
                    File.WriteAllText(PublicKeyPath, rsa.ToXmlString(false));
                }
                RefreshKeyStatus();
                _tabs.SelectedIndex = 1;
                MessageBox.Show(this,
                    "تم إنشاء المفتاحين بنجاح في:\r\n" + KeyDir + "\r\n\r\n" +
                    "لا تنسَ نسخ public_key.xml إلى ActivationManager.PublicKeyXml وإعادة بناء برنامج الـ POS.\r\n\r\n" +
                    "Key pair generated successfully.\r\n" +
                    "Remember to paste public_key.xml into ActivationManager.PublicKeyXml and rebuild the POS.",
                    "تم / Done",
                    MessageBoxButtons.OK, MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button1, RtlOptions());
            }
            catch (Exception ex)
            {
                ShowError("تعذر توليد المفاتيح:\r\n" + ex.Message);
            }
        }

        private void OpenKeyFolder()
        {
            try { Process.Start("explorer.exe", "\"" + KeyDir.TrimEnd('\\') + "\""); }
            catch { /* تجاهل */ }
        }

        private void RefreshKeyStatus()
        {
            bool priv = File.Exists(PrivateKeyPath);
            bool pub = File.Exists(PublicKeyPath);

            _lblPrivState.Text = priv
                ? "private_key.xml  —  موجود / present  (سرّ مطلق، لا يُوزَّع أبداً / never distribute)"
                : "private_key.xml  —  غير موجود / missing";
            _lblPrivState.ForeColor = priv ? Theme.Success : Theme.Danger;

            _lblPubState.Text = pub
                ? "public_key.xml  —  موجود / present"
                : "public_key.xml  —  غير موجود / missing";
            _lblPubState.ForeColor = pub ? Theme.Success : Theme.Danger;

            if (_lblStatus != null)
            {
                if (priv)
                {
                    _lblStatus.Text = "المفاتيح جاهزة — يمكنك إصدار الرخص / Keys ready — you can issue licenses";
                    _lblStatus.ForeColor = Theme.Success;
                }
                else
                {
                    _lblStatus.Text = "لا توجد مفاتيح — ولّدها من تبويب «المفاتيح» أولاً / No keys yet — generate them from the Keys tab first";
                    _lblStatus.ForeColor = Theme.Danger;
                }
            }
        }

        private void ShowError(string msg)
        {
            MessageBox.Show(this, msg, "خطأ / Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1, RtlOptions());
        }

        private static MessageBoxOptions RtlOptions()
        {
            return MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;
        }
    }
}