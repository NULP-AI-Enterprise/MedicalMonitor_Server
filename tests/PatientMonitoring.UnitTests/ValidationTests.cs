using System.ComponentModel.DataAnnotations;
using PatientMonitoring.Api.Contracts;

namespace PatientMonitoring.UnitTests;

public class ValidationTests
{
    private static IList<ValidationResult> ValidateModel(object model)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model, serviceProvider: null, items: null);
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);

        if (model is IValidatableObject validatable)
        {
            var customResults = validatable.Validate(context);
            results.AddRange(customResults);
        }

        return results;
    }

    [Fact]
    public void RecordVitalSignsRequest_WhenAllFieldsNull_FailsValidation()
    {
        // Arrange
        var request = new RecordVitalSignsRequest();

        // Act
        var results = ValidateModel(request);

        // Assert
        Assert.Contains(results, r => r.ErrorMessage!.Contains("At least one vital sign value must be provided"));
    }

    [Fact]
    public void RecordVitalSignsRequest_WhenDiastolicGreaterOrEqualSystolic_FailsValidation()
    {
        // Arrange
        var request = new RecordVitalSignsRequest
        {
            SystolicBloodPressure = 120,
            DiastolicBloodPressure = 120
        };

        // Act
        var results = ValidateModel(request);

        // Assert
        Assert.Contains(results, r => r.ErrorMessage!.Contains("lower than systolic"));
    }

    [Fact]
    public void RecordVitalSignsRequest_WhenRecordedAtFarInFuture_FailsValidation()
    {
        // Arrange
        var request = new RecordVitalSignsRequest
        {
            HeartRate = 80,
            RecordedAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };

        // Act
        var results = ValidateModel(request);

        // Assert
        Assert.Contains(results, r => r.ErrorMessage!.Contains("future"));
    }

    [Fact]
    public void RecordVitalSignsRequest_WhenValidValues_PassesValidation()
    {
        // Arrange
        var request = new RecordVitalSignsRequest
        {
            HeartRate = 75,
            SystolicBloodPressure = 120,
            DiastolicBloodPressure = 80,
            OxygenSaturation = 98.5,
            Temperature = 36.6,
            RespiratoryRate = 16
        };

        // Act
        var results = ValidateModel(request);

        // Assert
        Assert.Empty(results);
    }
}
