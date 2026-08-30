using Microsoft.Extensions.Options;

namespace ApiPipeline.NET;

// Source-generated validators: trimming and AOT safe; they also run IValidatableObject.Validate.

/// <summary>Validates <see cref="ApiPipelineCorrelationIdOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineCorrelationIdOptionsValidator : IValidateOptions<ApiPipelineCorrelationIdOptions>;

/// <summary>Validates <see cref="ApiPipelineRateLimitingOptions"/>, including each policy.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineRateLimitingOptionsValidator : IValidateOptions<ApiPipelineRateLimitingOptions>;

/// <summary>Validates <see cref="ApiPipelineEdgeRateLimitingOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineEdgeRateLimitingOptionsValidator : IValidateOptions<ApiPipelineEdgeRateLimitingOptions>;

/// <summary>Validates <see cref="ApiPipelineResponseCachingOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineResponseCachingOptionsValidator : IValidateOptions<ApiPipelineResponseCachingOptions>;

/// <summary>Validates <see cref="ApiPipelineSecurityHeadersOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineSecurityHeadersOptionsValidator : IValidateOptions<ApiPipelineSecurityHeadersOptions>;

/// <summary>Validates <see cref="ApiPipelineRequestLimitsOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineRequestLimitsOptionsValidator : IValidateOptions<ApiPipelineRequestLimitsOptions>;

/// <summary>Validates <see cref="ApiPipelineForwardedHeadersOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineForwardedHeadersOptionsValidator : IValidateOptions<ApiPipelineForwardedHeadersOptions>;

/// <summary>Validates <see cref="ApiPipelineRequestTimeoutsOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineRequestTimeoutsOptionsValidator : IValidateOptions<ApiPipelineRequestTimeoutsOptions>;

/// <summary>Validates <see cref="ApiPipelineOutputCachingOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineOutputCachingOptionsValidator : IValidateOptions<ApiPipelineOutputCachingOptions>;

/// <summary>Validates <see cref="ApiPipelineResponseCompressionOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ApiPipelineResponseCompressionOptionsValidator : IValidateOptions<ApiPipelineResponseCompressionOptions>;
