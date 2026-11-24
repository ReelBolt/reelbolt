using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class TypeSafeDecisionClientTests
{
    [Fact]
    public async Task DecideAsync_WithChoiceQuestion_SendsCorrectRequest()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>
        {
            new DecisionQuestion(
                Name: "choice-q1",
                Kind: DecisionKind.Choice,
                Instructions: "Pick one option",
                Options: ["option-a", "option-b", "option-c"])
        };

        var responseObj = new
        {
            model = "jev-latest",
            answers = new Dictionary<string, object>
            {
                {
                    "choice-q1",
                    new
                    {
                        type = "choice",
                        choice = "option-a",
                        probabilities = new { option_a = 0.7, option_b = 0.2, option_c = 0.1 },
                        confidence = 0.85
                    }
                }
            },
            usage = new { input_tokens = 100, output_tokens = 50 }
        };
        var responseBody = JsonSerializer.Serialize(responseObj);

        var mockHandler = new MockHttpMessageHandler(responseBody);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act
        var result = await client.DecideAsync(state, questions, CancellationToken.None);

        // Assert
        Assert.NotNull(mockHandler.LastRequest);
        Assert.NotNull(mockHandler.LastRequestBody);
        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody);
        var root = doc.RootElement;

        Assert.Equal("Sample context", root.GetProperty("state").GetString());
        Assert.Equal("jev-latest", root.GetProperty("model").GetString());

        var questionsObj = root.GetProperty("questions").EnumerateObject().ToList();
        Assert.Single(questionsObj);
        Assert.Equal("choice-q1", questionsObj[0].Name);

        var question = questionsObj[0].Value;
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.Equal("Pick one option", question.GetProperty("instructions").GetString());

        var criteria = question.GetProperty("criteria");
        Assert.Equal("description", criteria.GetProperty("option-a").GetString());
        Assert.Equal("description", criteria.GetProperty("option-b").GetString());
        Assert.Equal("description", criteria.GetProperty("option-c").GetString());
    }

    [Fact]
    public async Task DecideAsync_WithScoreQuestion_SendsCorrectRequest()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>
        {
            new DecisionQuestion(
                Name: "score-q1",
                Kind: DecisionKind.Score,
                Instructions: "Rate the quality",
                Options: ["poor", "fair", "good", "excellent"])
        };

        var responseObj = new
        {
            model = "jev-latest",
            answers = new Dictionary<string, object>
            {
                {
                    "score-q1",
                    new
                    {
                        type = "score",
                        score = 2,
                        legend = new { poor = "Very poor quality", fair = "Acceptable quality", good = "Good quality", excellent = "Excellent quality" },
                        probabilities = new { poor = 0.1, fair = 0.2, good = 0.4, excellent = 0.3 },
                        confidence = 0.75
                    }
                }
            },
            usage = new { input_tokens = 100, output_tokens = 50 }
        };
        var responseBody = JsonSerializer.Serialize(responseObj);

        var mockHandler = new MockHttpMessageHandler(responseBody);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act
        var result = await client.DecideAsync(state, questions, CancellationToken.None);

        // Assert
        Assert.NotNull(mockHandler.LastRequest);
        Assert.NotNull(mockHandler.LastRequestBody);
        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody);
        var root = doc.RootElement;

        var questionsObj = root.GetProperty("questions").EnumerateObject().ToList();
        var question = questionsObj[0].Value;
        Assert.Equal("score", question.GetProperty("type").GetString());

        var criteria = question.GetProperty("criteria");
        var criteriaArray = criteria.EnumerateArray().ToList();
        Assert.Equal(4, criteriaArray.Count);
        Assert.Equal("poor", criteriaArray[0].GetString());
        Assert.Equal("fair", criteriaArray[1].GetString());
        Assert.Equal("good", criteriaArray[2].GetString());
        Assert.Equal("excellent", criteriaArray[3].GetString());
    }

    [Fact]
    public async Task DecideAsync_WithNoulQuestion_SendsCorrectRequest()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>
        {
            new DecisionQuestion(
                Name: "noul-q1",
                Kind: DecisionKind.Noul,
                Instructions: "Make a decision",
                Options: [])
        };

        var responseObj = new
        {
            model = "jev-latest",
            answers = new Dictionary<string, object>
            {
                {
                    "noul-q1",
                    new
                    {
                        type = "noul",
                        noul = 42
                    }
                }
            },
            usage = new { input_tokens = 100, output_tokens = 50 }
        };
        var responseBody = JsonSerializer.Serialize(responseObj);

        var mockHandler = new MockHttpMessageHandler(responseBody);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act
        var result = await client.DecideAsync(state, questions, CancellationToken.None);

        // Assert
        Assert.NotNull(mockHandler.LastRequest);
        Assert.NotNull(mockHandler.LastRequestBody);
        using var doc = JsonDocument.Parse(mockHandler.LastRequestBody);
        var root = doc.RootElement;

        var questionsObj = root.GetProperty("questions").EnumerateObject().ToList();
        var question = questionsObj[0].Value;
        Assert.Equal("noul", question.GetProperty("type").GetString());
        Assert.False(question.TryGetProperty("criteria", out _));
    }

    [Fact]
    public async Task DecideAsync_ParsesChoiceAnswer()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>
        {
            new DecisionQuestion(
                Name: "choice-q1",
                Kind: DecisionKind.Choice,
                Instructions: "Pick one",
                Options: ["a", "b", "c"])
        };

        var responseObj = new
        {
            model = "jev-latest",
            answers = new Dictionary<string, object>
            {
                {
                    "choice-q1",
                    new
                    {
                        type = "choice",
                        choice = "a",
                        probabilities = new { a = 0.7, b = 0.2, c = 0.1 },
                        confidence = 0.85
                    }
                }
            },
            usage = new { input_tokens = 100, output_tokens = 50 }
        };
        var responseBody = JsonSerializer.Serialize(responseObj);

        var mockHandler = new MockHttpMessageHandler(responseBody);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act
        var result = await client.DecideAsync(state, questions, CancellationToken.None);

        // Assert
        Assert.Equal("jev-latest", result.Model);
        Assert.Equal(100, result.InputTokens);
        Assert.Equal(50, result.OutputTokens);

        var answer = result.Answers["choice-q1"];
        Assert.Equal("choice-q1", answer.Name);
        Assert.Equal(DecisionKind.Choice, answer.Kind);
        Assert.Equal("a", answer.Choice);
        Assert.Null(answer.Score);
        Assert.Null(answer.Noul);
        Assert.Equal(0.85, answer.Confidence);
        Assert.Equal(3, answer.Probabilities.Count);
        Assert.Equal(0.7, answer.Probabilities["a"]);
        Assert.Equal(0.2, answer.Probabilities["b"]);
        Assert.Equal(0.1, answer.Probabilities["c"]);
    }

    [Fact]
    public async Task DecideAsync_ParsesScoreAnswer()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>
        {
            new DecisionQuestion(
                Name: "score-q1",
                Kind: DecisionKind.Score,
                Instructions: "Rate it",
                Options: ["poor", "fair", "good"])
        };

        var responseObj = new
        {
            model = "jev-latest",
            answers = new Dictionary<string, object>
            {
                {
                    "score-q1",
                    new
                    {
                        type = "score",
                        score = 2,
                        legend = new { poor = "Poor quality", fair = "Fair quality", good = "Good quality" },
                        probabilities = new { poor = 0.1, fair = 0.3, good = 0.6 },
                        confidence = 0.92
                    }
                }
            },
            usage = new { input_tokens = 100, output_tokens = 50 }
        };
        var responseBody = JsonSerializer.Serialize(responseObj);

        var mockHandler = new MockHttpMessageHandler(responseBody);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act
        var result = await client.DecideAsync(state, questions, CancellationToken.None);

        // Assert
        var answer = result.Answers["score-q1"];
        Assert.Equal(DecisionKind.Score, answer.Kind);
        Assert.Null(answer.Choice);
        Assert.Equal(2.0, answer.Score);
        Assert.Null(answer.Noul);
        Assert.Equal(0.92, answer.Confidence);
        Assert.Equal(3, answer.Probabilities.Count);
    }

    [Fact]
    public async Task DecideAsync_ParsesNoulAnswer()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>
        {
            new DecisionQuestion(
                Name: "noul-q1",
                Kind: DecisionKind.Noul,
                Instructions: "Decide",
                Options: [])
        };

        var responseObj = new
        {
            model = "jev-latest",
            answers = new Dictionary<string, object>
            {
                {
                    "noul-q1",
                    new
                    {
                        type = "noul",
                        noul = (int?)42
                    }
                }
            },
            usage = new { input_tokens = 100, output_tokens = 50 }
        };
        var responseBody = JsonSerializer.Serialize(responseObj);

        var mockHandler = new MockHttpMessageHandler(responseBody);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act
        var result = await client.DecideAsync(state, questions, CancellationToken.None);

        // Assert
        var answer = result.Answers["noul-q1"];
        Assert.Equal(DecisionKind.Noul, answer.Kind);
        Assert.Null(answer.Choice);
        Assert.Null(answer.Score);
        Assert.Equal(42.0, answer.Noul);
        Assert.Null(answer.Confidence);
    }

    [Fact]
    public async Task DecideAsync_Throws401_OnUnauthorized()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>();

        var mockHandler = new MockHttpMessageHandler("Unauthorized", HttpStatusCode.Unauthorized);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DecideAsync(state, questions, CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Unauthorized", ex.Message);
    }

    [Fact]
    public async Task DecideAsync_Throws422_OnUnprocessableEntity()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>();

        var mockHandler = new MockHttpMessageHandler("Invalid request", HttpStatusCode.UnprocessableEntity);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DecideAsync(state, questions, CancellationToken.None));

        Assert.Contains("422", ex.Message);
        Assert.Contains("Invalid request", ex.Message);
    }

    [Fact]
    public async Task DecideAsync_Throws429_OnRateLimited()
    {
        // Arrange
        var state = new DecisionState("Sample context");
        var questions = new List<DecisionQuestion>();

        var mockHandler = new MockHttpMessageHandler("Too many requests", HttpStatusCode.TooManyRequests);
        var httpClient = new HttpClient(mockHandler)
        {
            BaseAddress = new Uri("https://api.typesafe.ai/")
        };

        var client = new TypeSafeDecisionClient(httpClient, "jev-latest");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DecideAsync(state, questions, CancellationToken.None));

        Assert.Contains("429", ex.Message);
        Assert.Contains("Too many requests", ex.Message);
    }

    /// <summary>
    /// Mock HttpMessageHandler for testing without network calls.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public MockHttpMessageHandler(string responseBody = "", HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json")
            };

            return await Task.FromResult(response);
        }
    }
}
