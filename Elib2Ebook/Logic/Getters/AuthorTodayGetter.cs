using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Elib2Ebook.Configs;
using Elib2Ebook.Types.AuthorToday.Response;
using Elib2Ebook.Types.Book;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
using Elib2Ebook.Extensions;

namespace Elib2Ebook.Logic.Getters; 

public class AuthorTodayGetter : GetterBase {
    private readonly Regex _userIdRgx = new("userId: (?<userId>\\d+),");

    public AuthorTodayGetter(BookGetterConfig config) : base(config) { }

    protected override Uri SystemUrl => new("https://author.today/");
    
    // cloudflare :(
    private const string IP = "185.26.98.227";
    
    protected override string GetId(Uri url) {
        return url.Segments[2].Trim('/');
    }

    /// <summary>
    /// Получение книги
    /// </summary>
    /// <param name="url">Ссылка на книгу</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public override async Task<Book> Get(Uri url) {
        var bookId = GetId(url);

        var bookUri = new Uri($"https://author.today/work/{bookId}").ReplaceHost(IP);
        Console.WriteLine($"Загружаю книгу {bookUri.ToString().CoverQuotes()}"); 
        var doc = await GetHtmlDocument(bookUri);

        var book = new Book(bookUri) {
            Cover = await GetCover(doc, bookUri),
            Chapters = await FillChapters(bookId, GetUserId(doc)),
            Title = doc.GetTextBySelector("h1"),
            Author = GetAuthor(doc, url),
            Annotation = doc.QuerySelector("div.rich-content")?.InnerHtml,
            Seria = GetSeria(doc)
        };
        
        return book;
    }

    private static Author GetAuthor(HtmlDocument doc, Uri url) {
        var author = doc.QuerySelector("div.book-authors a");
        return new Author(author.GetTextBySelector(), new Uri(url, author.Attributes["href"]?.Value ?? string.Empty));
    }

    private static Seria GetSeria(HtmlDocument doc) {
        var a = doc.QuerySelector("div.book-meta-panel a[href^=/work/series/]");
        if (a != default) {
            var seria = new Seria();
            seria.Name = a.GetTextBySelector();
            
            var numberText = a.GetTextBySelector("+ span");
            if (!string.IsNullOrWhiteSpace(numberText) && numberText.StartsWith("#")) {
                seria.Number = numberText.Trim('#');
            }

            return seria;
        }

        return default;
    }

    /// <summary>
    /// Получение идентификатора пользователя из контента
    /// </summary>
    /// <param name="doc"></param>
    /// <returns></returns>
    private string GetUserId(HtmlDocument doc) {
        var match = _userIdRgx.Match(doc.ParsedText);
        return match.Success ? match.Groups["userId"].Value : string.Empty;
    }

    private MultipartFormDataContent GenerateAuthData(string token) {
        return new() {
            {new StringContent(token), "__RequestVerificationToken"},
            {new StringContent(_config.Login), "Login"},
            {new StringContent(_config.Password), "Password"}
        };
    }

    /// <summary>
    /// Авторизация в системе
    /// </summary>
    /// <exception cref="Exception"></exception>
    public override async Task Authorize(){
        if (!_config.HasCredentials) {
            return;
        }

        var doc = await _config.Client.GetHtmlDocWithTriesAsync(new Uri("https://author.today/"));
        var token = doc.QuerySelector("[name=__RequestVerificationToken]")?.Attributes["value"]?.Value;

        using var post = await PostAsync(new Uri("https://author.today/account/login").ReplaceHost(IP), GenerateAuthData(token));
        var response = await post.Content.ReadFromJsonAsync<ApiResponse<object>>();
            
        if (response?.IsSuccessful == true) {
            Console.WriteLine("Успешно авторизовались");
        } else {
            throw new Exception($"Не удалось авторизоваться. {response?.Messages?.FirstOrDefault()}");
        }
    }

    /// <summary>
    /// Получение обложки
    /// </summary>
    /// <param name="doc">HtmlDocument</param>
    /// <param name="bookUri">Адрес страницы с книгой</param>
    /// <returns></returns>
    private Task<Image> GetCover(HtmlDocument doc, Uri bookUri) {
        var imagePath = doc.QuerySelector("img.cover-image")?.Attributes["src"]?.Value;
        return !string.IsNullOrWhiteSpace(imagePath) ? GetImage(new Uri(bookUri, imagePath)) : Task.FromResult(default(Image));
    }

    /// <summary>
    /// Получение списка частей из кода страницы
    /// </summary>
    /// <param name="bookId">Идентификатор книги</param>
    /// <returns></returns>
    private async Task<List<Chapter>> GetChapters(string bookId) {
        var bookUri = new Uri($"https://author.today/reader/{bookId}").ReplaceHost(IP);
        var doc = await GetHtmlDocument(bookUri);
        
        const string START_PATTERN = "chapters:";
        var startIndex = doc.ParsedText.IndexOf(START_PATTERN, StringComparison.Ordinal) + START_PATTERN.Length;
        var endIndex = doc.ParsedText.IndexOf("}],", startIndex, StringComparison.Ordinal) + 2;
        var metaContent = doc.ParsedText[startIndex..endIndex].Trim().TrimEnd(';', ')');
        return metaContent.Deserialize<List<Chapter>>();
    }

    /// <summary>
    /// Дозагрузка различных пареметров частей
    /// </summary>
    /// <param name="bookId">Идентификатор книги</param>
    /// <param name="userId">Идентификатор пользователя</param>
    private async Task<IEnumerable<Chapter>> FillChapters(string bookId, string userId) {
        var chapters = await GetChapters(bookId);
            
        foreach (var chapter in chapters) {
            var chapterUri = new Uri($"https://author.today/reader/{bookId}/chapter?id={chapter.Id}").ReplaceHost(IP);
                
            Console.WriteLine($"Получаем главу {chapter.Title.CoverQuotes()}");
            using var response = await GetAsync(chapterUri);

            var secret = GetSecret(response, userId);
            if (string.IsNullOrWhiteSpace(secret)) {
                Console.WriteLine($"Невозможно расшифровать главу {chapter.Title.CoverQuotes()}. Возможно, платный доступ.");
                continue;
            }
                
            Console.WriteLine($"Расшифровываем главу {chapter.Title.CoverQuotes()}. Секрет {secret.CoverQuotes()}");
            var decodeText = Decode(await GetText(response), secret);
                
            // Порядок вызова функций важен. В методе GetImages происходит
            // исправления урлов картинок для их отображения в epub документе
            var chapterDoc = decodeText.AsHtmlDoc();
            chapter.Images = await GetImages(chapterDoc, chapterUri);
            chapter.Content = chapterDoc.DocumentNode.InnerHtml;
        }
            
        return chapters;
    }

    /// <summary>
    /// Получение секрета для расшифровки контента книги
    /// </summary>
    /// <param name="response">Ответ сервера</param>
    /// <param name="userId">Идентификатор пользователя</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private static string GetSecret(HttpResponseMessage response, string userId) {
        if (!response.Headers.Contains("Reader-Secret")) {
            return string.Empty;
        }
            
        foreach (var header in response.Headers.GetValues("Reader-Secret")) {
            return string.Join("", header.Reverse()) + "@_@" + userId;
        }

        return string.Empty;
    }

    /// <summary>
    /// Получение зашифрованного текста главы книги
    /// </summary>
    /// <param name="response">Ответ сервера</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    private static async Task<string> GetText(HttpResponseMessage response) {
        var data = await response.Content.ReadFromJsonAsync<ApiResponse<ChapterData>>();
        if (data?.IsSuccessful == false) {
            throw new Exception($"Не удалось получить контент части. {data.Messages.FirstOrDefault()}");
        }

        return data?.Data.Text ?? string.Empty;
    }

    /// <summary>
    /// Расшифровка контента главы книги с использованием ключа
    /// </summary>
    /// <param name="secret"></param>
    /// <param name="encodedText"></param>
    /// <returns></returns>
    private static string Decode(string encodedText, string secret) {
        var sb = new StringBuilder();
        for (var i = 0; i < encodedText.Length; i++) {
            sb.Append((char) (encodedText[i] ^ secret[i % secret.Length]));
        }

        return sb.ToString().HtmlDecode();
    }
    
    protected override HttpRequestMessage GetImageRequestMessage(Uri uri) {
        if (!uri.Host.Contains(SystemUrl.Host) && uri.Host != IP) {
            return base.GetImageRequestMessage(uri);
        }

        var replaceHost = uri.Host == SystemUrl.Host ? SystemUrl.Host : uri.Host;
        var message = new HttpRequestMessage(HttpMethod.Get, uri.ReplaceHost(IP));
        message.Headers.Add("Host", replaceHost);
        return message;
    }
    
    private HttpRequestMessage CreateRequestMessage(Uri uri, HttpContent content = null) {
        var message = new HttpRequestMessage(content == null ? HttpMethod.Get : HttpMethod.Post, uri);
        message.Headers.Add("Host", SystemUrl.Host);
        return message;
    }

    private Task<HttpResponseMessage> GetAsync(Uri uri) {
        return _config.Client.SendWithTriesAsync(() => CreateRequestMessage(uri));
    }
    
    private Task<HttpResponseMessage> PostAsync(Uri uri, HttpContent content) {
        return _config.Client.SendWithTriesAsync(() => CreateRequestMessage(uri, content));
    }

    private async Task<HtmlDocument> GetHtmlDocument(Uri uri) {
        var response = await GetAsync(uri);
        var content = await response.Content.ReadAsStringAsync();
            
        return content.AsHtmlDoc();
    }
}