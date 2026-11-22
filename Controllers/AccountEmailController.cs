using MattsRecords.Models;
using MattsRecords.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MimeKit;

namespace MattsRecords.Controllers;

[Route("Account")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class AccountEmailController(ICollectionAccounts accounts, IAccountEmail emails, IAccountMail mail,
    ILogger<AccountEmailController> logger) : Controller
{
    [Authorize, HttpGet("Email")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        if (CollectionSession.GetId(User) is not Guid id)
            return Forbid();
        return View(await PopulateAsync(id, new AccountEmailForm(), cancellationToken));
    }

    [Authorize, HttpPost("Email")]
    [EnableRateLimiting(RecoveryController.RecoveryRateLimit)]
    public async Task<IActionResult> Index(AccountEmailForm form, CancellationToken cancellationToken)
    {
        if (CollectionSession.GetId(User) is not Guid id)
            return Forbid();
        if (ModelState.IsValid)
        {
            var address = form.Address.Trim();
            var account = await accounts.FindByIdAsync(id, cancellationToken);
            if (!mail.Configured)
                ModelState.AddModelError(string.Empty, "Email delivery is awaiting setup by the site owner.");
            else if (!MailboxAddress.TryParse(address, out var mailbox) || mailbox.Address != address)
                ModelState.AddModelError(nameof(form.Address), "Enter a single email address.");
            else if (account is null || !accounts.Verify(account, form.Password))
                ModelState.AddModelError(nameof(form.Password), "The current password is incorrect.");
            else if (await emails.BeginAsync(account, address, cancellationToken) is not { } code)
                ModelState.AddModelError(string.Empty, "Wait a minute, then request a new verification email.");
            else
            {
                try
                {
                    await mail.SendAsync(address, "Verify your Record Room email",
                        "Confirm that this email address belongs to you. The link expires in 24 hours.",
                        "/Account/VerifyEmail", code, cancellationToken);
                    TempData["Status"] = "Verification email sent. Open the link to confirm this address.";
                    return RedirectToAction(nameof(Index));
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Verification email delivery failed ({ErrorType}).", error.GetType().Name);
                    ModelState.AddModelError(string.Empty, "The site couldn’t send the verification email. Try again in a minute.");
                }
            }
        }
        return View(await PopulateAsync(id, form, cancellationToken));
    }

    [AllowAnonymous, HttpGet("VerifyEmail")]
    public IActionResult Verify()
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return View(new ConfirmEmailForm());
    }

    [AllowAnonymous, HttpPost("VerifyEmail")]
    [EnableRateLimiting(RecoveryController.RecoveryRateLimit)]
    public async Task<IActionResult> Verify(ConfirmEmailForm form, CancellationToken cancellationToken)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!ModelState.IsValid)
            return View(form);
        if (!await emails.ConfirmAsync(form.Token, cancellationToken))
        {
            ModelState.AddModelError(string.Empty, "This verification link is invalid or has expired. Request a new one from account settings.");
            return View(form);
        }
        TempData["Status"] = "Email verified. Password reset emails will go to this address.";
        return RedirectToAction("Index", "Home");
    }

    private async Task<AccountEmailForm> PopulateAsync(Guid id, AccountEmailForm form, CancellationToken cancellationToken)
    {
        var addresses = await emails.ReadAsync(id, cancellationToken);
        form.Verified = addresses.Verified;
        form.Pending = addresses.Pending;
        if (string.IsNullOrEmpty(form.Address) && addresses.Pending is { } pending)
            form.Address = pending;
        form.MailConfigured = mail.Configured;
        return form;
    }
}
