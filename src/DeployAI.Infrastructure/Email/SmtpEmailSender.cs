using System.Net;
using System.Net.Mail;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeployAI.Infrastructure.Email;

public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string body, CancellationToken cancellationToken);
}

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task SendAsync(string toAddress, string subject, string body, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.SmtpHost))
        {
            _logger.LogInformation(
                "Email skipped (SMTP not configured). To={To} Subject={Subject} Body={Body}",
                toAddress,
                subject,
                body);
            return Task.CompletedTask;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(toAddress);

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.UseSsl,
            Credentials = string.IsNullOrWhiteSpace(_options.SmtpUsername)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword)
        };

        client.Send(message);
        return Task.CompletedTask;
    }
}
