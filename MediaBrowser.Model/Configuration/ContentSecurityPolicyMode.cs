namespace MediaBrowser.Model.Configuration
{
    /// <summary>
    /// How the full Content-Security-Policy is sent with every response. The <c>frame-ancestors</c>
    /// restriction is sent in every mode as long as the security headers are enabled.
    /// </summary>
    public enum ContentSecurityPolicyMode
    {
        /// <summary>
        /// No full policy: only <c>frame-ancestors 'self'</c> is sent, as before the policy existed.
        /// </summary>
        Off = 0,

        /// <summary>
        /// The full policy is sent as <c>Content-Security-Policy-Report-Only</c>: browsers report violations
        /// to the server but block nothing. The enforcing <c>frame-ancestors 'self'</c> header stays.
        /// </summary>
        ReportOnly = 1,

        /// <summary>
        /// The full policy is sent as <c>Content-Security-Policy</c> and browsers block what it forbids.
        /// </summary>
        Enforce = 2,
    }
}
