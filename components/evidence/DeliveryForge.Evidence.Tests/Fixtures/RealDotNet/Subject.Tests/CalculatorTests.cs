using RealDotNet.Subject;
using Xunit;

namespace RealDotNet.Subject.Tests;

public sealed class CalculatorTests
{
    [Fact]
    public void Adds_two_numbers() => Assert.Equal(4, Calculator.Add(1, 3));
}
