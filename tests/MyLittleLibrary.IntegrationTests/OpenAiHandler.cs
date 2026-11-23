using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MyLittleLibrary.IntegrationTests;

internal sealed class OpenAiHandler : HttpMessageHandler
{
    private int requests;

    public int RequestCount => Volatile.Read(ref requests);
    public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public string BooksJson { get; set; } = "{\"books\":[]}";
    public TimeSpan Delay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        RequestStarted.TrySetResult();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var root = body.RootElement;
        Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal("book_scan", format.GetProperty("name").GetString());
        Assert.Contains("books", format.GetProperty("schema").GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        var message = Assert.Single(root.GetProperty("input").EnumerateArray());
        var content = message.GetProperty("content").EnumerateArray().ToArray();
        Assert.Contains(content, item => item.GetProperty("type").GetString() == "input_text");
        var image = Assert.Single(content, item => item.GetProperty("type").GetString() == "input_image");
        Assert.StartsWith("data:image/png;base64,", image.GetProperty("image_url").GetString());

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        var responseBody = JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = BooksJson } }
                }
            }
        });
        return new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}
