namespace KlaviyoCRC;

public class BrandOptions
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // Klaviyo
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://a.klaviyo.com/api";
    public string ProfilesEndpoint { get; set; } = "/profiles";
    public string Accept { get; set; } = "application/vnd.api+json";
    public string Revision { get; set; } = string.Empty;
    public int MaxPagesToFetch { get; set; } = 0;

    // Base de datos: tabla y stored procedures propios de la marca
    public string CustomerType { get; set; } = string.Empty;
    public string InsertCustomersSp { get; set; } = string.Empty;
    public string GetCustomerEmailsForCrcSp { get; set; } = string.Empty;
    public string UpdateEmailExclusionCrcSp { get; set; } = string.Empty;
    public string EmailExclusionType { get; set; } = string.Empty;
    public string GetCustomerPhonesForCrcSp { get; set; } = string.Empty;
    public string UpdatePhoneExclusionCrcSp { get; set; } = string.Empty;
    public string PhoneExclusionType { get; set; } = string.Empty;
}