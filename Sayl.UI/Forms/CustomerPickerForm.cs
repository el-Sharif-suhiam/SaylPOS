using Common.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// اختيار عميل (بحث + اختيار + إضافة سريعة).
    /// بعد الإغلاق بـ OK تكون الخاصية SelectedCustomer قد عُيِّنت.
    /// </summary>
    public class CustomerPickerForm : DialogForm
    {
        private TextBox _txtSearch = null!;
        private DataGridView _grid = null!;
        private Button _btnPick = null!;

        private List<(int PartyId, string Name)> _rows = new();

        public int? SelectedCustomerId { get; private set; }
        public string? SelectedCustomerName { get; private set; }

        public CustomerPickerForm()
        {
            BuildUi();
            Load += async (_, _) => await LoadCustomersAsync("");
        }

        private void BuildUi()
        {
            Text = "اختيار العميل";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 600);
            MinimumSize = new Size(500, 440);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;

            // تخطيط متجاوب: شريط علوي (عنوان + بحث) ثم شبكة تتمدد ثم أزرار سفلية
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = AppTheme.CardBg,
                Padding = new Padding(16, 12, 16, 12)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // العنوان + البحث
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // الشبكة
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));   // الأزرار

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = AppTheme.CardBg, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

            var lblTitle = new Label
            {
                Text = "اختيار العميل",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary,
                Margin = new Padding(0, 0, 8, 0)
            };

            _txtSearch = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 11F),
                Margin = new Padding(4, 8, 4, 8)
            };
            _txtSearch.PlaceholderText = "ابحث بالاسم أو الجوال ثم Enter";

            var btnSearch = AppTheme.MakeButton("بحث", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            btnSearch.Dock = DockStyle.Fill;
            btnSearch.Margin = new Padding(0, 6, 0, 6);
            btnSearch.Click += async (_, _) => await LoadCustomersAsync(_txtSearch.Text?.Trim() ?? "");

            header.Controls.Add(lblTitle, 0, 0);
            header.Controls.Add(_txtSearch, 1, 0);
            header.Controls.Add(btnSearch, 2, 0);

            _grid = new DataGridView { Dock = DockStyle.Fill };
            UiHelpers.StyleGrid(_grid);
            _grid.ReadOnly = false; // نضيف زر اختيار لكل صف
            _grid.Columns.Add("colId", "الرقم");
            _grid.Columns.Add("colName", "الاسم");
            _grid.Columns.Add("colPhone", "الجوال");
            _grid.Columns["colId"].FillWeight = 20;
            _grid.Columns["colName"].FillWeight = 45;
            _grid.Columns["colPhone"].FillWeight = 25;
            var colPick = new DataGridViewButtonColumn
            {
                Name = "colPick",
                HeaderText = "",
                Text = "اختيار",
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.Flat,
                FillWeight = 10
            };
            colPick.DefaultCellStyle.ForeColor = AppTheme.Primary;
            _grid.Columns.Add(colPick);
            _grid.CellClick += Grid_CellClick;
            _grid.CellDoubleClick += (_, _) => PickSelected();

            _btnPick = AppTheme.MakeButton("إضافة عميل جديد", AppTheme.SidebarBg, hoverColor: AppTheme.SidebarHover);
            _btnPick.Dock = DockStyle.Fill;
            _btnPick.Margin = new Padding(0, 6, 4, 0);
            _btnPick.Click += async (_, _) => await QuickAddCustomerAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Dock = DockStyle.Fill;
            btnClose.Margin = new Padding(4, 6, 0, 0);
            btnClose.DialogResult = DialogResult.Cancel;

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = AppTheme.CardBg, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            footer.Controls.Add(_btnPick, 0, 0);
            footer.Controls.Add(btnClose, 1, 0);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(footer, 0, 2);

            Controls.Add(root);
            CancelButton = btnClose;
            AcceptButton = btnSearch;
        }

        private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == _grid.Columns["colPick"].Index)
                PickSelected();
        }

        private void PickSelected()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _rows.Count)
            {
                UiHelpers.ShowError(this, "اختر عميلًا من القائمة.");
                return;
            }

            var row = _rows[_grid.CurrentRow.Index];
            SelectedCustomerId = row.PartyId;
            SelectedCustomerName = row.Name;
            DialogResult = DialogResult.OK;
        }

        private async Task LoadCustomersAsync(string search)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var customerService = scope.ServiceProvider.GetRequiredService<clsCustomerService>();
                var customers = await customerService.GetCustomersListAsync(search);

                _rows = customers
                    .Where(c => c.Party != null && c.Party.IsActive)
                    .Select(c => (c.PartyId, c.Party.Name))
                    .ToList();

                _grid.Rows.Clear();
                foreach (var row in _rows)
                {
                    var party = customers.First(c => c.PartyId == row.PartyId).Party;
                    _grid.Rows.Add(row.PartyId, row.Name, party!.Phone1 ?? "-");
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل العملاء. تحقق من الاتصال بقاعدة البيانات.");
            }
        }

        private async Task QuickAddCustomerAsync()
        {
            using var input = new QuickNameForm();
            if (input.ShowDialog(this) != DialogResult.OK || input.ResultValue == null)
                return;

            var dto = new DtoParty
            {
                PartyType = 'C',
                Name = input.ResultValue.Value.name,
                Phone1 = string.IsNullOrWhiteSpace(input.ResultValue.Value.phone) ? null : input.ResultValue.Value.phone,
                IsActive = true
            };

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                result = await partyService.AddNewPartyWithRoleAsync(dto);
            }

            if (!result.IsSuccess)
            {
                UiHelpers.ShowError(this, string.IsNullOrWhiteSpace(result.Message) ? "فشلت إضافة العميل." : result.Message);
                return;
            }

            _txtSearch.Text = dto.Name;
            await LoadCustomersAsync(dto.Name);
        }

        /// <summary>نافذة إدخال سريعة: اسم + جوال.</summary>
        private class QuickNameForm : Form
        {
            public (string name, string phone)? ResultValue { get; private set; }

            public QuickNameForm()
            {
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(380, 220);
                MaximizeBox = false;
                MinimizeBox = false;
                RightToLeft = RightToLeft.Yes;
                Font = AppTheme.FontBody;
                Text = "عميل جديد";

                var lblName = new Label { Text = "الاسم *", Bounds = new Rectangle(24, 16, 332, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
                var txtName = new TextBox { Bounds = new Rectangle(24, 40, 332, 36), Font = AppTheme.FontBody };
                var lblPhone = new Label { Text = "الجوال", Bounds = new Rectangle(24, 86, 332, 22), TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.TextMuted };
                var txtPhone = new TextBox { Bounds = new Rectangle(24, 110, 332, 36), Font = AppTheme.FontBody };

                var btnOk = AppTheme.MakeButton("إضافة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
                btnOk.Bounds = new Rectangle(24, 158, 180, 46);
                var btnCancel = AppTheme.MakeButton("إلغاء", AppTheme.Bg, foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
                btnCancel.Bounds = new Rectangle(216, 158, 140, 46);
                btnCancel.DialogResult = DialogResult.Cancel;

                btnOk.Click += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(txtName.Text))
                    {
                        UiHelpers.ShowError(this, "الاسم مطلوب.");
                        return;
                    }
                    ResultValue = (txtName.Text.Trim(), txtPhone.Text?.Trim() ?? "");
                    DialogResult = DialogResult.OK;
                };

                Controls.AddRange(new Control[] { lblName, txtName, lblPhone, txtPhone, btnOk, btnCancel });
                AcceptButton = btnOk;
                CancelButton = btnCancel;
            }
        }
    }
}
