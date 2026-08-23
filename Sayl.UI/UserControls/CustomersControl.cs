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

        protected override string Title => "العملاء";

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var customerService = scope.ServiceProvider.GetRequiredService<clsCustomerService>();
                _customers = await customerService.GetCustomersListAsync(TxtSearch.Text?.Trim() ?? "");

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", "الاسم");
                Grid.Columns.Add("colPhone", "الجوال");
                Grid.Columns.Add("colBalance", "الرصيد");
                Grid.Columns.Add("colCredit", "حد الائتمان");
                Grid.Columns.Add("colStatus", "الحالة");
                Grid.Columns["colName"].FillWeight = 32;
                Grid.Columns["colPhone"].FillWeight = 22;
                Grid.Columns["colBalance"].FillWeight = 16;
                Grid.Columns["colCredit"].FillWeight = 16;
                Grid.Columns["colStatus"].FillWeight = 14;

                Grid.Rows.Clear();
                foreach (var c in _customers.Where(c => c.Party != null))
                    Grid.Rows.Add(c.Party!.Name, c.Party.Phone1 ?? "-", UiHelpers.Money(c.Balance ?? 0),
                        UiHelpers.Money(c.CreditLimit ?? 0), c.Party.IsActive ? "نشط" : "معطل");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل العملاء.");
            }
        }

        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة عميل", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditParty(null), 140);
            AddToolbarGap();
            AddToolbarButton("تعديل", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton("تعطيل", AppTheme.Danger, Color.FromArgb(185, 28, 28), async (_, _) => await DeactivateAsync(), 110);
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _customers.Where(c => c.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, "اختر عميلًا من القائمة.");
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
                UiHelpers.ShowError(this, "اختر عميلًا من القائمة.");
                return;
            }

            var party = visible[idx].Party!;
            if (!ConfirmationDialog.Show(this, "تعطيل عميل", $"سيتم تعطيل العميل '{party.Name}'. هل أنت متأكد؟"))
                return;

            Common.Results.Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var partyService = scope.ServiceProvider.GetRequiredService<clsPartyService>();
                result = await partyService.DeactivatePartyAsync(party.PartyId);
            }

            UiHelpers.ShowResult(this, result);
            if (result.IsSuccess)
                await LoadDataAsync();
        }
    }
}
