using Common.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم العملاء: عرض، بحث، إضافة/تعديل، تعطيل.</summary>
    internal class CustomersControl : SectionListControl
    {
        private List<Customer> _customers = new();

        protected override string Title => AppLang.T("Customers.Section.Title");

        // مفتاح «عرض المعطّلين» — الافتراضي: العملاء النشطون فقط
        protected override bool SupportsShowDeleted => true;

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var customerService = scope.ServiceProvider.GetRequiredService<clsCustomerService>();
                var paged = await customerService.GetCustomersPagedAsync(
                    TxtSearch.Text?.Trim() ?? "", includeInactive: ShowInactive,
                    page: _currentPage, pageSize: _pageSize);
                if (!ApplyPaged(paged))
                    return;
                _customers = paged.Items;

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", AppLang.T("Common.Label.Name"));
                Grid.Columns.Add("colPhone", AppLang.T("Common.Label.Phone"));
                Grid.Columns.Add("colBalance", AppLang.T("Customers.Label.Balance"));
                Grid.Columns.Add("colCredit", AppLang.T("Customers.Label.CreditLimit"));
                Grid.Columns.Add("colStatus", AppLang.T("Common.Label.Status"));
                Grid.Columns["colName"].FillWeight = 30;
                Grid.Columns["colPhone"].FillWeight = 20;
                Grid.Columns["colBalance"].FillWeight = 15;
                Grid.Columns["colCredit"].FillWeight = 15;
                Grid.Columns["colStatus"].FillWeight = 12;

                var colDetails = new DataGridViewButtonColumn
                {
                    Name = "colDetails",
                    HeaderText = "",
                    Text = AppLang.T("Common.Button.Details"),
                    UseColumnTextForButtonValue = true,
                    FlatStyle = FlatStyle.Flat,
                    FillWeight = 8
                };
                colDetails.DefaultCellStyle.ForeColor = AppTheme.Primary;
                colDetails.DefaultCellStyle.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
                Grid.Columns.Add(colDetails);

                Grid.Rows.Clear();
                foreach (var c in _customers.Where(c => c.Party != null))
                    Grid.Rows.Add(c.Party!.Name, c.Party.Phone1 ?? "-", UiHelpers.Money(c.Balance ?? 0),
                        UiHelpers.Money(c.CreditLimit ?? 0),
                        c.Party.IsActive ? AppLang.T("Common.Status.Active") : AppLang.T("Common.Status.Inactive"));
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Customers.Msg.LoadFailed"));
            }
        }

        // 📖 أزرار القسم — وصفة AddToolbarButton في SectionListControl.
        // «تفاصيل» عمود زر داخل الجدول (Grid_CellClick أدناه) يفتح كشف
        // الحساب — نفس نمط أعمدة الأزرار الموحد (راجع InvoicesControl).
        // VULN-33: «تحصيل دفعة» كان مفقودًا كليًا — ديون العملاء تنمو
        // بلا أي طريق سداد داخل المنتج (الخدمة ReceiveCustomerPaymentAsync
        // كانت جاهزة وبلا مستدعٍ واحد). الزر يستدعيها بعد تحصيل المبلغ.
        protected override void BuildToolbar()
        {
            AddToolbarButton(AppLang.T("Customers.Button.Add"), AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditParty(null), 140);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Customers.Button.Collect"), AppTheme.Success, Color.FromArgb(21, 128, 61), async (_, _) => await CollectPaymentAsync(), 130);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Common.Button.Edit"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton(AppLang.T("Customers.Button.Deactivate"), AppTheme.Danger, Color.FromArgb(185, 28, 28), async (_, _) => await DeactivateAsync(), 110);

            if (SessionState.Can(Common.Utils.AppPermissions.Customers))
            {
                AddToolbarGap();
                AddToolbarButton(AppLang.T("Customers.Button.Activate"), AppTheme.Success, Color.FromArgb(21, 128, 61),
                    async (_, _) => await ActivateAsync(), 130);
            }

            Grid.CellClick += Grid_CellClick;
        }

        /// <summary>VULN-33: تحصيل دفعة من رصيد عميل مدين —
        /// مسار التحصيل اليومي الغائب: يسأل المبلغ والطريقة ثم يستدعي
        /// ReceiveCustomerPaymentAsync (Serializable + سقف الرصيد داخلية).</summary>
        private async Task CollectPaymentAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _customers.Where(c => c.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Customers.Msg.PickCustomer"));
                return;
            }

            var customer = visible[idx];
            decimal balance = customer.Balance ?? 0;
            if (balance <= 0)
            {
                UiHelpers.ShowInfo(this, AppLang.T("Customers.Msg.NoDueBalance", customer.Party!.Name));
                return;
            }

            // نافذة دفع مبسطة للتحصيل: المبلغ + الطريقة (نستخدم PaymentForm بلا تقسيم)
            using var form = new PaymentForm(balance, hasCustomer: true)
            {
                Text = AppLang.T("Customers.Collect.Title")
            };
            // نغلف النتيجة بدفعة واحدة مسماة «تحصيل دفعة رصيد»
            if (form.ShowDialog(FindForm()) != DialogResult.OK || form.PaymentsList.Count == 0)
                return;

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var paymentService = scope.ServiceProvider.GetRequiredService<clsPaymentService>();
                result = await paymentService.ReceiveCustomerPaymentAsync(new Common.DTOs.RequestModels.DtoPaymentRequest
                {
                    PartyId = customer.PartyId,
                    Amount = form.PaymentsList.Sum(p => p.Amount),
                    Method = form.PaymentsList[0].Method,
                    PaymentDate = AppClock.Today,
                    Notes = AppLang.T("Customers.Collect.Notes", customer.Party!.Name)
                });
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }

        private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _customers.Count) return;
            if (Grid.Columns[e.ColumnIndex].Name != "colDetails") return;

            var visible = _customers.Where(c => c.Party != null).ToList();
            if (e.RowIndex >= visible.Count) return;

            var customer = visible[e.RowIndex];
            var party = customer.Party!;
            using var form = new PartyDetailsForm(party,
                (AppLang.T("Customers.Details.Balance"), UiHelpers.Money(customer.Balance ?? 0)),
                (AppLang.T("Customers.Details.CreditLimit"), UiHelpers.Money(customer.CreditLimit ?? 0)));
            form.ShowDialog(FindForm());
        }

        private async Task ActivateAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _customers.Where(c => c.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Customers.Msg.PickCustomer"));
                return;
            }

            var party = visible[idx].Party!;
            if (party.IsActive)
            {
                UiHelpers.ShowInfo(this, AppLang.T("Customers.Msg.AlreadyActive", party.Name));
                return;
            }

            if (!ConfirmationDialog.Show(this, AppLang.T("Customers.Title.Activate"),
                    AppLang.T("Customers.Msg.ActivateConfirm", party.Name), dangerStyle: false))
                return;

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                result = await partyService.ActivatePartyAsync(party.PartyId, SessionState.CurrentUser?.UserId ?? 0); // VULN-26: بوابة خدمية
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _customers.Where(c => c.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Customers.Msg.PickCustomer"));
                return;
            }
            EditParty(visible[idx].Party);
        }

        private void EditParty(Sayl.Core.Models.Party? existing)
        {
            DtoParty? dto = null;
            if (existing != null)
            {
                dto = new DtoParty
                {
                    PartyId = existing.PartyId,
                    PartyType = existing.PartyType,
                    Name = existing.Name,
                    Phone1 = existing.Phone1,
                    Phone2 = existing.Phone2,
                    Email = existing.Email,
                    AddressLine1 = existing.AddressLine1,
                    Notes = existing.Notes,
                    IsActive = existing.IsActive,
                    CreatedAt = existing.CreatedAt
                };
            }

            using var form = new PartyEditForm('C', dto);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task DeactivateAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _customers.Where(c => c.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, AppLang.T("Customers.Msg.PickCustomer"));
                return;
            }

            var party = visible[idx].Party!;
            if (!ConfirmationDialog.Show(this, AppLang.T("Customers.Title.Deactivate"),
                    AppLang.T("Customers.Msg.DeactivateConfirm", party.Name)))
                return;

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                result = await partyService.DeactivatePartyAsync(party.PartyId, SessionState.CurrentUser?.UserId ?? 0); // VULN-26: بوابة خدمية
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }
    }
}
