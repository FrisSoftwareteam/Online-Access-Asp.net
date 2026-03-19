using Clear;
using FirstReg.Core;
using FirstReg.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FirstReg.OnlineAccess.Controllers;

[Authorize]
[Route("admin")]
public class FRAdminController(ILogger<FRAdminController> logger, Service service, IApiClient apiClient, EStockApiUrl apiUrl, Mongo mongo)
        : BaseController(service, AuditLogSection.FrAdmin)
{
        private async Task<RegSH> GetShareholderDetailsModel(int regid, int accno)
        {
                var holdingsQuery = service.Data.GetAsQueryable<ShareHolding>()
                        .Include(x => x.Register)
                        .Include(x => x.Shareholder)
                        .ThenInclude(x => x.User)
                        .Where(x => x.RegisterId == regid);

                var holding = await holdingsQuery
                        .FirstOrDefaultAsync(x => x.AccountNo == accno.ToString());

                // Some account numbers are stored with leading zeroes while the UI passes them as numbers.
                holding ??= holdingsQuery
                        .AsEnumerable()
                        .FirstOrDefault(x => int.TryParse(x.AccountNo, out var dbAccNo) && dbAccNo == accno);

                if (holding == null)
                        throw new InvalidOperationException("The selected shareholder was not found.");

                var model = new RegSH
                {
                        Id = holding.Id,
                        RegCode = holding.RegisterId,
                        Register = holding.Register?.Name ?? "",
                        AccountNo = int.TryParse(holding.AccountNo, out var parsedAccNo) ? parsedAccNo : accno,
                        ClearingNo = holding.Shareholder?.ClearingNo ?? "",
                        Gender = "",
                        Phone = holding.Shareholder?.SecondaryPhone ?? "",
                        Mobile = holding.Shareholder?.PrimaryPhone ?? "",
                        Email = holding.Shareholder?.User?.Email ?? "",
                        Address1 = holding.Shareholder?.Street ?? "",
                        Address2 = holding.Shareholder?.State ?? "",
                        City = holding.Shareholder?.City ?? "",
                        TotalUnits = holding.Units,
                        Units = new List<Bson.Unit>(),
                        Dividends = new List<Bson.Dividend>()
                };

                var fullName = holding.AccountName?.Trim();
                if (string.IsNullOrWhiteSpace(fullName))
                        fullName = holding.Shareholder?.FullName?.Trim();

                if (!string.IsNullOrWhiteSpace(fullName))
                {
                        var nameParts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (nameParts.Length == 1)
                        {
                                model.LastName = nameParts[0];
                        }
                        else
                        {
                                model.LastName = nameParts[^1];
                                model.FirstName = nameParts[0];
                                model.MiddleName = string.Join(" ", nameParts.Skip(1).Take(nameParts.Length - 2));
                        }
                }

                if (holding.ShareHolderId > 0)
                {
                        var bsonShareholder = mongo.Find<Bson.Shareholder, int>(holding.ShareHolderId, MongoTables.Shareholders)
                                .FirstOrDefault();

                        var bsonHolding = bsonShareholder?.Holdings?.FirstOrDefault(x =>
                                x.RegCode == regid &&
                                string.Equals(x.AccountNo, holding.AccountNo, StringComparison.OrdinalIgnoreCase));

                        if (bsonHolding != null)
                        {
                                model.ClearingNo = string.IsNullOrWhiteSpace(model.ClearingNo) ? bsonHolding.ClearingNo : model.ClearingNo;
                                model.FirstName = string.IsNullOrWhiteSpace(model.FirstName) ? bsonHolding.FirstName : model.FirstName;
                                model.MiddleName = string.IsNullOrWhiteSpace(model.MiddleName) ? bsonHolding.MiddleName : model.MiddleName;
                                model.LastName = string.IsNullOrWhiteSpace(model.LastName) ? bsonHolding.LastName : model.LastName;
                                model.Address1 = string.IsNullOrWhiteSpace(model.Address1) ? bsonHolding.Address1 : model.Address1;
                                model.Address2 = string.IsNullOrWhiteSpace(model.Address2) ? bsonHolding.Address2 : model.Address2;
                                model.Units = bsonHolding.Units ?? new List<Bson.Unit>();
                                model.Dividends = bsonHolding.Dividends ?? new List<Bson.Dividend>();
                                model.TotalUnits = model.Units.Any() ? model.Units.Sum(x => x.TotalUnits) : holding.Units;
                        }
                }

                if (!model.Units.Any())
                {
                        model.Units.Add(new Bson.Unit
                        {
                                Id = holding.Id,
                                AccountNo = model.AccountNo,
                                RegCode = regid,
                                CertNo = 0,
                                Date = holding.Date.ToString("dd-MMM-yyyy"),
                                OldCertNo = "",
                                Description = "Opening balance",
                                Narration = "Opening balance",
                                TotalUnits = holding.Units
                        });
                }

                return model;
        }

        public IActionResult Index()
        {
                return RedirectToAction(nameof(Shareholders));
        }


        [Route("shareholders")]
        public async Task<IActionResult> Shareholders(int regid, string global,
                string name, string addr, string cscs, string acc, string oldacc)
        {
                try
                {
                        await LogAuditAction(AuditLogType.Search,
                                $"{User.Identity.Name} searched with these parameters: RegCode={regid}, Global={global}, " +
                                $"Name={name}, Address={addr}, CSCS={cscs}, Account={acc}, OldAccount={oldacc}");

                        return View(new FRRegisterSHSumm()
                        {
                                RegId = regid,
                                Global = global,
                                Name = name,
                                Address = addr,
                                ClearingNo = cscs,
                                AccountNo = acc,
                                OldAccountNo = oldacc,
                                ListUrl = Url.Action(nameof(GetShareholderLists), new { regid, global, name, addr, cscs, acc, oldacc }),
                                ExportUrl = "",
                                DetailsUrl = Url.Action(nameof(GetShareholderDetails)),
                                DividendsUrl = Url.Action(nameof(GetShareholderDetails))
                        });
                }
                catch (Exception ex)
                {
                        logger.LogError(ex.ToString());
                        TempData["error"] = ex.Message;
                        return View(new FRRegisterSHSumm());
                }
        }

        [HttpGet("shareholders-list")]
        public async Task<IActionResult> GetShareholderLists(
                int regid, string global, string name, string addr, string cscs, string acc, string oldacc)
        {
                return await GetShareholderListz(regid, global, name, addr, cscs, acc, oldacc);
        }

        [HttpGet("shareholders-list/l")]
        public async Task<IActionResult> GetShareholderListz(int regid, string global,
                string name, string addr, string cscs, string acc, string oldacc)
        {
                try
                {
                        IQueryable<ShareHolding> query = service.Data.GetAsQueryable<ShareHolding>()
                                .Where(x => x.AccountName != null && x.AccountName != "");

                        if (regid > 0)
                                query = query.Where(x => x.RegisterId == regid);
                        if (!string.IsNullOrWhiteSpace(name))
                                query = query.Where(x => x.AccountName.Contains(name));
                        if (!string.IsNullOrWhiteSpace(acc))
                                query = query.Where(x => x.AccountNo.Contains(acc));
                        if (!string.IsNullOrWhiteSpace(global))
                                query = query.Where(x => x.AccountName.Contains(global) || x.AccountNo.Contains(global));
                        if (!string.IsNullOrWhiteSpace(cscs))
                                query = query.Where(x => x.Shareholder.ClearingNo.Contains(cscs));
                        if (!string.IsNullOrWhiteSpace(addr))
                                query = query.Where(x => x.Shareholder.Street.Contains(addr) || x.Shareholder.City.Contains(addr));

                        var rows = await query
                                .OrderBy(x => x.AccountName)
                                .Take(5000)
                                .Select(x => new
                                {
                                        x.Id,
                                        x.RegisterId,
                                        x.AccountNo,
                                        x.AccountName,
                                        x.Units,
                                        RegisterName = x.Register.Name,
                                        ClearingNo = x.Shareholder.ClearingNo,
                                        Phone = x.Shareholder.PrimaryPhone
                                })
                                .ToListAsync();

                        return Ok(new
                        {
                                data = rows.Select(x => new[]
                                {
                                        string.IsNullOrEmpty(x.ClearingNo) ? x.AccountNo : $"{x.AccountNo}<br>{x.ClearingNo}",
                                        string.IsNullOrEmpty(x.Phone) ? x.AccountName : $"{x.AccountName}<br/><span class=\"fs-7 fw-normal\">{x.Phone}</span>",
                                        x.Units.ToString("N0"),
                                        x.RegisterName ?? "",
                                        x.AccountNo,
                                        x.Id.ToString(),
                                        x.RegisterId.ToString(),
                                        x.RegisterName ?? ""
                                }).ToList()
                        });
                }
                catch (Exception ex)
                {
                        logger.LogError($"Error fetching shareholders from DB: {Clear.Tools.GetAllExceptionMessage(ex)}");
                        return StatusCode(StatusCodes.Status500InternalServerError, Clear.Tools.GetAllExceptionMessage(ex));
                }
        }

        [HttpGet("shareholder/{regid?}/{accno?}")]
        public async Task<IActionResult> GetShareholderDetails(int regid, int accno)
        {
                try
                {
                        var sh = await GetShareholderDetailsModel(regid, accno);

                        await LogAuditAction(AuditLogType.ViewShareholder,
                                $"{User.Identity.Name} viewed the details of this account: RegCode={regid}, " +
                                $"Account={accno}, Name={sh.FullName}, CHN={sh.ClearingNo}, Units={sh.TotalUnits}");

                        return Ok(new RegisterHolderModel(sh));
                }
                catch (InvalidOperationException ex)
                {
                        logger.LogWarning($"Shareholder details lookup failed: {Clear.Tools.GetAllExceptionMessage(ex)};");
                        return NotFound(ex.Message);
                }
                catch (Exception ex)
                {
                        logger.LogError($"Error: {Clear.Tools.GetAllExceptionMessage(ex)};");
                        return StatusCode(StatusCodes.Status500InternalServerError, Clear.Tools.GetAllExceptionMessage(ex));
                }
        }

        [HttpGet("shareholder/{regid?}/{accno?}/download")]
        public async Task<IActionResult> DownloadShareholderDetails(int regid, int accno)
        {
                try
                {
                        var sh = await GetShareholderDetailsModel(regid, accno);

                        await LogAuditAction(AuditLogType.DownloadShareholder,
                                $"{User.Identity.Name} downloaded the details of this account: RegCode={regid}, " +
                                $"Account={accno}, Name={sh.FullName}, CHN={sh.ClearingNo}, Units={sh.TotalUnits}");

                        var regmodel = new RegisterHolderModel(sh);

                        var stream = Tools.ExportToXml(regmodel);

                        byte[] fileContent = stream.ToArray(); // simpler way of converting to array
                        stream.Close();

                        return File(fileContent, "application/force-download", $"shareholder-{accno}-{DateTime.Now:yyyMMddHHmmss}.xlsx");
                }
                catch (InvalidOperationException ex)
                {
                        logger.LogWarning($"Shareholder details download lookup failed: {Clear.Tools.GetAllExceptionMessage(ex)};");
                        return NotFound(ex.Message);
                }
                catch (Exception ex)
                {
                        logger.LogError($"Error: {Clear.Tools.GetAllExceptionMessage(ex)};");
                        return StatusCode(StatusCodes.Status500InternalServerError, Clear.Tools.GetAllExceptionMessage(ex));
                }
        }

        #region profile

        [Route("profile")]
        public async Task<IActionResult> Profile() => View(new UserModel(
                await service.Data.Get<User>(x => x.UserName.ToLower() == User.Identity.Name.ToLower())));

        [Route("profile/update")]
        public async Task<IActionResult> UpdateProfile(UserModel model)
        {
                try
                {
                        var user = await service.Data.Get<User>(x => x.UserName.ToLower() == User.Identity.Name.ToLower());

                        await LogAuditAction(AuditLogType.ProfileUpdate,
                                $"{User.Identity.Name} Updated their profile");

                        user.FullName = model.FullName.Trim();
                        //user.UserName = model.Email.Trim();
                        //user.Email = model.Email.Trim();
                        user.PhoneNumber = model.MobileNo.Trim();
                        user.StockBroker.Street = model.Street.Trim();
                        user.StockBroker.City = model.City.Trim();
                        user.StockBroker.State = model.State.Trim();
                        //user.StockBroker.Country = model.Country.Trim();
                        user.StockBroker.SecondaryPhone = model.SecondaryPhone.Trim();
                        user.StockBroker.Fax = model.PostCode.Trim();

                        await service.Data.UpdateAsync(user);

                        TempData["success"] = "Your profile was updated";

                        return RedirectToAction("Profile");
                }
                catch (Exception ex)
                {
                        TempData["error"] = Clear.Tools.GetAllExceptionMessage(ex);
                }
                return Redirect(Request.Headers[Tools.UrlReferrer].ToString());
        }

        #endregion
}
