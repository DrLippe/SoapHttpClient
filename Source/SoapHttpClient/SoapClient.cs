using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using SoapHttpClient.Enums;
using SoapHttpClient.DTO;
using System.Xml.Serialization;
using System.Xml;

namespace SoapHttpClient;

public interface ISoapClient
{
    /// <summary>
    /// Posts an asynchronous message.
    /// </summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="soapVersion">The preferred SOAP version.</param>
    /// <param name="bodies">The body of the SOAP message.</param>
    /// <param name="headers">The header of the SOAP message.</param>
    /// <param name="action">The SOAPAction of the SOAP message.</param>
    Task<HttpResponseMessage> PostAsync(Uri endpoint, SoapVersion soapVersion, IEnumerable<XElement> bodies, IEnumerable<XElement>? headers = null, string? action = null, CancellationToken cancellationToken = default(CancellationToken));
}

public class SoapClient : ISoapClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="SoapClient" /> class.
    /// </summary>
    /// <param name="IHttpClientFactory">Microsoft.Extensions.Http HttpClientFactory</param>
    public SoapClient(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    public SoapClient()
        => _httpClientFactory = DefaultHttpClientFactory();

    /// <inheritdoc />
    public Task<HttpResponseMessage> PostAsync(
        Uri endpoint,
        SoapVersion soapVersion,
        IEnumerable<XElement> bodies,
        IEnumerable<XElement>? headers = null,
        string? action = null,
        CancellationToken cancellationToken = default(CancellationToken))
    {
        if (endpoint == null)
            throw new ArgumentNullException(nameof(endpoint));

        if (bodies == null)
            throw new ArgumentNullException(nameof(bodies));

        if (!bodies.Any())
            throw new ArgumentException("Bodies element cannot be empty", nameof(bodies));

        // Get configuration based on version
        var messageConfiguration = new SoapMessageConfiguration(soapVersion);

        // Get the envelope
        var envelope = GetEnvelope(messageConfiguration);

        // Add headers
        if (headers != null && headers.Any())
            envelope.Add(new XElement(messageConfiguration.Schema + "Header", headers));

        // Add bodies
        envelope.Add(new XElement(messageConfiguration.Schema + "Body", bodies));

        // Get HTTP content
        var content = new StringContent(envelope.ToString(), Encoding.UTF8, messageConfiguration.MediaType);

        // Add SOAP action if any
        if (action != null)
        {
            content.Headers.Add("SOAPAction", action);

            if (messageConfiguration.SoapVersion == SoapVersion.Soap12)
                content.Headers.ContentType!.Parameters.Add(
                    new NameValueHeaderValue("ActionParameter", $"\"{action}\""));
        }

        // Execute call
        var httpClient = _httpClientFactory.CreateClient(nameof(SoapClient));
        return httpClient.PostAsync(endpoint, content, cancellationToken);
    }


    public T Read<T>(HttpResponseMessage message, SoapVersion soapVersion)
    {
        var config = new SoapMessageConfiguration(soapVersion);

        if (message == null)
            throw new ArgumentNullException();
        if (!message.IsSuccessStatusCode)
            throw new ArgumentException($"The provided message is not a success message: {message.StatusCode} {message.ReasonPhrase}");

        var settings = new XmlReaderSettings() { DtdProcessing = DtdProcessing.Prohibit };

        using (var reader = XmlReader.Create(message.Content.ReadAsStream(), settings))
        {
            reader.MoveToContent();

            // envelope
            if (string.IsNullOrEmpty(reader.NamespaceURI))
                reader.ReadStartElement("Envelope");
            else
                reader.ReadStartElement("Envelope", config.Schema.NamespaceName);
            reader.MoveToContent();

            // body
            reader.ReadStartElement("Body", reader.NamespaceURI);
            reader.MoveToContent();

            // response
            if (reader.IsStartElement("Fault", reader.NamespaceURI))
                throw new Exception();

            var xmlSerializer = new XmlSerializer(typeof(T));
            xmlSerializer.UnknownElement += (s, e) => Console.WriteLine($"found element {e.Element.Name} and expected {e.ExpectedElements}");

            T result = (T)xmlSerializer.Deserialize(reader);

            return result;
        }
    }

    #region Private Methods

    private static XElement GetEnvelope(SoapMessageConfiguration soapMessageConfiguration)
    {
        return new
            XElement(
                soapMessageConfiguration.Schema + "Envelope",
                new XAttribute(
                    XNamespace.Xmlns + "soapenv",
                    soapMessageConfiguration.Schema.NamespaceName));
    }

    private static IHttpClientFactory DefaultHttpClientFactory()
    {
        var serviceProvider = new ServiceCollection();

        serviceProvider
            .AddHttpClient(nameof(SoapClient))
            .ConfigurePrimaryHttpMessageHandler(e =>
                new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
                });

        return serviceProvider.BuildServiceProvider().GetService<IHttpClientFactory>()!;
    }

    #endregion Private Methods
}
