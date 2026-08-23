using Common.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Models;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم الموردين: عرض، بحث، إضافة/تعديل، تعطيل.</summary>
    internal class SuppliersControl : SectionListControl
    {
        private List<Supplier> _suppliers = new();

        protected override string Title => "الموردون";

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var supplierService = scope.ServiceProvider.GetRequiredService<clsSupplierService>();
                _suppliers = await supplierService.GetSupplierListAsync(TxtSearch.Text?.Trim() ?? "");

                Grid.Columns.Clear();
                Grid.Columns.Add("colName", "الاسم");
                Grid.Columns.Add("colPhone", "الجوال");
                Grid.Columns.Add("colCategory", "الفئة");
                Grid.Columns.Add("colBalance", "الرصيد");
                Grid.Columns.Add("colStatus", "الحالة");
                Grid.Columns["colName"].FillWeight = 32;
                Grid.Columns["colPhone"].FillWeight = 22;
                Grid.Columns["colCategory"].FillWeight = 16;
                Grid.Columns["colBalance"].FillWeight = 16;
                Grid.Columns["colStatus"].FillWeight = 14;

                Grid.Rows.Clear();
                foreach (var s in _suppliers.Where(s => s.Party != null))
                    Grid.Rows.Add(s.Party!.Name, s.Party.Phone1 ?? "-", s.Category ?? "-",
                        UiHelpers.Money(s.Balance ?? 0), s.Party.IsActive ? "نشط" : "معطل");
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الموردين.");
            }
        }

        protected override void BuildToolbar()
        {
            AddToolbarButton("إضافة مورد", AppTheme.Primary, AppTheme.PrimaryDark, (_, _) => EditParty(null), 140);
            AddToolbarGap();
            AddToolbarButton("تعديل", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => EditSelected(), 110);
            AddToolbarGap();
            AddToolbarButton("تعطيل", AppTheme.Danger, Color.FromArgb(185, 28, 28), async (_, _) => await DeactivateAsync(), 110);
        }

        private void EditSelected()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _suppliers.Where(s => s.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, "اختر موردًا من القائمة.");
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

            using var form = new PartyEditForm('S', dto);
            if (form.ShowDialog(FindForm()) == DialogResult.OK)
                _ = LoadDataAsync();
        }

        private async Task DeactivateAsync()
        {
            int idx = Grid.CurrentRow?.Index ?? -1;
            var visible = _suppliers.Where(s => s.Party != null).ToList();
            if (idx < 0 || idx >= visible.Count)
            {
                UiHelpers.ShowError(this, "اختر موردًا من القائمة.");
                return;
            }

            var party = visible[idx].Party!;
            if (!ConfirmationDialog.Show(this, "تعطيل مورد", $"سيتم تعطيل المورد '{party.Name}'. هل أنت متأكد؟"))
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
