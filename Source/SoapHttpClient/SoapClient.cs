using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using SoapHttpClient.Enums;
using SoapHttpClient.DTO;
using System.Xml.Serialization;
using System.Xml;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Data;
using System.Xml.XPath;
using System.Runtime.Serialization;

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
    public Uri Uri { get; set; }

    public SoapVersion SoapVersion { get; set; }

    public string ServiceNamespace { get; set; }

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
    public async Task<HttpResponseMessage> PostAsync(
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
        var body = envelope.ToString();

        Console.WriteLine("Sending Request:\n" + body + "\n");

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
        var response = await httpClient.PostAsync(endpoint, content, cancellationToken);

        return response;
    }

    public string Read(HttpResponseMessage message, SoapVersion soapVersion)
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

            // result
            reader.Read();

            var result = reader.ReadElementContentAsString();

            return result;
        }
    }

    public T Read<T>(HttpResponseMessage message, SoapVersion soapVersion)
    {
        var config = new SoapMessageConfiguration(soapVersion);
        var type = typeof(T);

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

    public object Invoke(string endpoint, object[] parameters, [CallerMemberName] string methodName = "")
    {
        var reflectedMethod = GetType().GetMethod(methodName);
        if (reflectedMethod == null)
            throw new InvalidOperationException(nameof(methodName));

        var returnType = reflectedMethod.ReturnType;
        var parameterInfos = reflectedMethod.GetParameters();
        var ns = XNamespace.Get(ServiceNamespace);

        // Serialize parameters
        var requestParameters = new List<XElement>();
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var parameterName = parameterInfos[i].Name!;
            var parameterType = parameterInfos[i].ParameterType;

            var serializer = new XmlSerializer(parameterType);
            var xDoc = new XDocument();

            using (var writer = xDoc.CreateWriter())
                serializer.Serialize(writer, parameter);

            requestParameters.Add(new XElement(ns.GetName(parameterName), xDoc.Root!.Nodes().InDocumentOrder()));
        }

        // create request body
        var body = new XElement(ns.GetName(endpoint), requestParameters.ToArray());

        var response = this.Post(Uri, SoapVersion, body);

        // deserialize response
        var responseString = response.Content.ReadAsStringAsync().Result;
        var xDocResponse = XDocument.Parse(responseString);

        // deserialize dataset
        if (returnType.BaseType == typeof(DataSet))
        {
            var result = (DataSet)Activator.CreateInstance(returnType)!;
            result.ReadXml(response.Content.ReadAsStream());

            return result;
        }

        // deserialize data contract
        if (returnType.GetCustomAttribute<DataContractAttribute>() != null)
        {
            var result = xDocResponse.Root!.Descendants().First(xn => xn.Name.ToString().EndsWith("Result"));
            result.Name = XName.Get(returnType.Name, ServiceNamespace);

            var serializer = new DataContractSerializer(returnType);

            return serializer.ReadObject(result.CreateReader());
        }

        // deserialize primitive
        if (returnType.IsArray)
        {
            var result = xDocResponse.Root!.Descendants().First(xn => xn.Name.ToString().EndsWith("Result"));
            var descendants = result.Descendants().ToArray();

            var elementType = returnType.GetElementType();
            var array = Array.CreateInstance(elementType, descendants.Length);

            for (int i = 0; i < array.Length; i++)
                array.SetValue(descendants[i].Value, i);

            return array;
        }

        else
        {
            var result = xDocResponse.Root!.Descendants().First(xn => xn.Name.ToString().EndsWith("Result")).Value;

            if (returnType == typeof(string))
                return result;
            if (returnType == typeof(char))
                return char.Parse(result);
            
            if (returnType == typeof(int))
                return int.Parse(result);
            if (returnType == typeof(long))
                return long.Parse(result);
            if (returnType == typeof(float))
                return float.Parse(result);
            if (returnType == typeof(double))
                return double.Parse(result);
            if (returnType == typeof(decimal))
                return decimal.Parse(result);

            if (returnType == typeof(bool))
                return bool.Parse(result);
            if (returnType == typeof(DateTime))
                return DateTime.Parse(result);
            if (returnType == typeof(Guid))
                return Guid.Parse(result);

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
                    XNamespace.Xmlns + "soap",
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
