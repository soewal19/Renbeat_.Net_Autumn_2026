using FluentAssertions;
using FluentValidation;
using RoomBooking.Shared.Dtos.Auth;
using RoomBooking.Shared.Dtos.Bookings;
using RoomBooking.Shared.Dtos.Resources;

namespace RoomBooking.UnitTests;

public class ValidatorTests
{
    [Theory]
    [InlineData("user@test.com", "Password123", "Test User", true)]
    [InlineData("", "Password123", "Test User", false)]
    [InlineData("not-an-email", "Password123", "Test User", false)]
    [InlineData("user@test.com", "", "Test User", false)]
    [InlineData("user@test.com", "123", "Test User", false)]
    [InlineData("user@test.com", "Password123", "", false)]
    public void RegisterRequestValidator_ShouldValidateCorrectly(
        string email, string password, string displayName, bool shouldBeValid)
    {
        var validator = new RegisterRequestValidator();
        var request = new RegisterRequest(email, password, displayName);
        var result = validator.Validate(request);
        result.IsValid.Should().Be(shouldBeValid);
    }

    [Fact]
    public void RegisterRequestValidator_ShouldFail_WhenDisplayNameIsNull()
    {
        var validator = new RegisterRequestValidator();
        var request = new RegisterRequest("user@test.com", "Password123", null!);
        var result = validator.Validate(request);
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("user@test.com", "Password123", true)]
    [InlineData("", "Password123", false)]
    [InlineData("not-email", "Password123", false)]
    [InlineData("user@test.com", "", false)]
    public void LoginRequestValidator_ShouldValidateCorrectly(
        string email, string password, bool shouldBeValid)
    {
        var validator = new LoginRequestValidator();
        var request = new LoginRequest(email, password);
        var result = validator.Validate(request);
        result.IsValid.Should().Be(shouldBeValid);
    }

    [Theory]
    [InlineData("Room A", "A meeting room", true)]
    [InlineData("", "A meeting room", false)]
    [InlineData("Room A", "", true)]
    public void CreateResourceRequestValidator_ShouldValidateCorrectly(
        string name, string description, bool shouldBeValid)
    {
        var validator = new CreateResourceRequestValidator();
        var request = new CreateResourceRequest(name, description);
        var result = validator.Validate(request);
        result.IsValid.Should().Be(shouldBeValid);
    }

    [Fact]
    public void CreateResourceRequestValidator_ShouldFail_WhenNameIsNull()
    {
        var validator = new CreateResourceRequestValidator();
        var request = new CreateResourceRequest(null!, "desc");
        var result = validator.Validate(request);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateResourceRequestValidator_ShouldPass_WhenDescriptionIsNull()
    {
        var validator = new CreateResourceRequestValidator();
        var request = new CreateResourceRequest("Room A", null!);
        var result = validator.Validate(request);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateResourceRequestValidator_ShouldRejectTooLongName()
    {
        var validator = new CreateResourceRequestValidator();
        var tooLongName = new string('x', 201);
        var request = new CreateResourceRequest(tooLongName, "desc");
        var result = validator.Validate(request);
        result.IsValid.Should().BeFalse();
    }
}

public class CreateBookingRequestValidatorTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(42, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void CreateBookingRequestValidator_ShouldValidateTimeSlotId(int timeSlotId, bool shouldBeValid)
    {
        var validator = new CreateBookingRequestValidator();
        var request = new CreateBookingRequest(timeSlotId);
        var result = validator.Validate(request);
        result.IsValid.Should().Be(shouldBeValid);
    }
}

public class CreateTimeSlotRequestValidatorTests
{
    [Fact]
    public void CreateTimeSlotRequestValidator_ShouldPass_WhenEndAfterStart()
    {
        var validator = new CreateTimeSlotRequestValidator();
        var start = new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var request = new CreateTimeSlotRequest(start, start.AddHours(1));
        var result = validator.Validate(request);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateTimeSlotRequestValidator_ShouldFail_WhenEndEqualsStart()
    {
        var validator = new CreateTimeSlotRequestValidator();
        var start = new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var request = new CreateTimeSlotRequest(start, start);
        var result = validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EndUtc");
    }

    [Fact]
    public void CreateTimeSlotRequestValidator_ShouldFail_WhenEndBeforeStart()
    {
        var validator = new CreateTimeSlotRequestValidator();
        var start = new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var request = new CreateTimeSlotRequest(start, start.AddHours(-1));
        var result = validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EndUtc");
    }
}

public class DbConcurrencyHelperTests
{
    [Fact]
    public void IsUniqueConstraintViolation_ShouldReturnFalse_ForUnrelatedException()
    {
        var ex = new InvalidOperationException("test");
        DbConcurrencyHelper.IsUniqueConstraintViolation(ex).Should().BeFalse();
    }

    [Fact]
    public void IsUniqueConstraintViolation_ShouldReturnFalse_ForNullException()
    {
        DbConcurrencyHelper.IsUniqueConstraintViolation(null).Should().BeFalse();
    }
}
