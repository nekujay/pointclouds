using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ScanPublisher
{
    internal class PublishOptions
    {
        public string FilePath;
        public string Title;
        public string Description = "";
        public double Voxel = 0;
        public string Profile = "web";
        public bool Push = true;
        public bool OpenInBrowser = false;
    }

    internal class GithubSettings
    {
        public string Owner;
        public string Repo;
        public string Branch = "main";
        public string Token;

        public static string SettingsPath
        {
            get { return Path.Combine(Pipeline.Root, "settings.json"); }
        }

        public static GithubSettings Load()
        {
            GithubSettings s = new GithubSettings();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string text = File.ReadAllText(SettingsPath, Encoding.UTF8);
                    string owner = Pipeline.JsonField(text, "owner");
                    string repo = Pipeline.JsonField(text, "repo");
                    string branch = Pipeline.JsonField(text, "branch");
                    string token = Pipeline.JsonField(text, "token");
                    if (owner != null) s.Owner = owner;
                    if (repo != null) s.Repo = repo;
                    if (branch != null && branch.Length > 0) s.Branch = branch;
                    if (token != null) s.Token = token;
                }
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(s.Owner) || string.IsNullOrEmpty(s.Repo))
            {
                // автоопределение от текущего remote
                try
                {
                    string remote = Pipeline.RunCapture("git", "remote get-url origin", Pipeline.Root, null, null);
                    System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(remote, @"github\.com[:/](?<o>[^/\s]+)/(?<r>[^/\s\.]+)");
                    if (m.Success)
                    {
                        s.Owner = m.Groups["o"].Value;
                        s.Repo = m.Groups["r"].Value;
                    }
                }
                catch (Exception) { }
            }
            return s;
        }

        public bool IsSet { get { return !string.IsNullOrEmpty(Owner) && !string.IsNullOrEmpty(Repo); } }

        public string RepoDisplay { get { return (Owner ?? "") + "/" + (Repo ?? ""); } }

        // базовый URL GitHub Pages для просмотра облака
        public string PagesBase
        {
            get { return "https://" + (Owner ?? "") + ".github.io/" + (Repo ?? ""); }
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"owner\": \"").Append(Pipeline.JsonEscape(Owner ?? "")).Append("\",\r\n");
            sb.Append("  \"repo\": \"").Append(Pipeline.JsonEscape(Repo ?? "")).Append("\",\r\n");
            sb.Append("  \"branch\": \"").Append(Pipeline.JsonEscape(Branch ?? "main")).Append("\",\r\n");
            sb.Append("  \"token\": \"").Append(Pipeline.JsonEscape(Token ?? "")).Append("\"\r\n");
            sb.Append("}\r\n");
            try { File.WriteAllText(SettingsPath, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
        }
    }

    internal class CloudEntry
    {
        public string Id = "";
        public string Title = "";
        public string Description = "";
        public string Src = "";
        public string Points = "";
        public string Date = "";
        public string Poster = "";
        public string Rcp = "";

        public static string FilePath
        {
            get { return Path.Combine(Pipeline.Root, "clouds.json"); }
        }

        public static List<CloudEntry> LoadAll()
        {
            List<CloudEntry> list = new List<CloudEntry>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                int start = text.IndexOf('[');
                int end = text.LastIndexOf(']');
                if (start < 0 || end <= start) return list;
                string body = text.Substring(start + 1, end - start - 1);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, "\\{[^{}]*\\}"))
                {
                    string o = m.Value;
                    CloudEntry e = new CloudEntry();
                    e.Id = Pipeline.JsonField(o, "id") ?? "";
                    e.Title = Pipeline.JsonField(o, "title") ?? "";
                    e.Description = Pipeline.JsonField(o, "description") ?? "";
                    e.Src = Pipeline.JsonField(o, "src") ?? "";
                    e.Points = Pipeline.IntField(o, "points") ?? "";
                    e.Date = Pipeline.JsonField(o, "date") ?? "";
                    e.Poster = Pipeline.JsonField(o, "poster") ?? "";
                    e.Rcp = Pipeline.JsonField(o, "rcp") ?? "";
                    list.Add(e);
                }
            }
            catch (Exception) { }
            return list;
        }

        public static void SaveAll(List<CloudEntry> entries)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[\r\n");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(",\r\n");
                CloudEntry e = entries[i];
                Pipeline.WriteEntry(sb, e.Id, e.Title, e.Description, e.Src,
                    string.IsNullOrEmpty(e.Points) ? null : e.Points,
                    e.Date, e.Poster, e.Rcp);
            }
            sb.Append("\r\n]\r\n");
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }

        public int PointsValue
        {
            get
            {
                int v;
                return int.TryParse(Points, out v) ? v : 0;
            }
        }
    }

    internal static class Pipeline
    {
        public static string Root
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        private static string ConverterExe
        {
            get { return Path.Combine(Root, @"tools\PotreeConverter\PotreeConverter_1.7_windows_x64\PotreeConverter.exe"); }
        }

        public static string DecimatorPath { get { return Path.Combine(Root, "decimate.py"); } }

        public static string Slug(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string map = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
            string[] lat = { "a","b","v","g","d","e","yo","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","c","ch","sh","sch","","y","","e","yu","ya" };
            StringBuilder sb = new StringBuilder();
            foreach (char ch in text.ToLowerInvariant())
            {
                int idx = map.IndexOf(ch);
                if (idx >= 0) sb.Append(lat[idx]);
                else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                else sb.Append('-');
            }
            string s = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (s.Length > 40) s = s.Substring(0, 40).Trim('-');
            if (s.Length == 0) s = "cloud-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return s;
        }

        internal static string RunCapture(string exe, string args, string workDir, Action<string> log, Action<int> progress)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            try { psi.StandardOutputEncoding = new UTF8Encoding(false); } catch (Exception) { }
            try { psi.StandardErrorEncoding = new UTF8Encoding(false); } catch (Exception) { }

            StringBuilder output = new StringBuilder();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler handler = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (output) { output.AppendLine(e.Data); }
                    string line = e.Data;
                    if (log != null)
                    {
                        Match m = Regex.Match(line, @"(\d{1,3})\s*%");
                        if (m.Success && progress != null)
                        {
                            int val;
                            if (int.TryParse(m.Groups[1].Value, out val) && val >= 0 && val <= 100) progress(val);
                        }
                        if (line.Length > 0) log(line);
                    }
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                lock (output) return output.ToString() + "|exit=" + p.ExitCode;
            }
        }

        private static void Run(string exe, string args, string workDir, Action<string> log, Action<int> progress)
        {
            string res = RunCapture(exe, args, workDir, log, progress);
            int idx = res.LastIndexOf("|exit=", StringComparison.Ordinal);
            string code = idx >= 0 ? res.Substring(idx + 6) : "0";
            if (code != "0") throw new Exception("Команда завершилась с кодом " + code + ": " + Path.GetFileName(exe));
        }

        public static string Publish(PublishOptions o, Action<string> log, Action<int> progress)
        {
            Action<string> say = delegate(string s) { if (log != null) log(s); };

            if (!File.Exists(o.FilePath)) throw new Exception("Не найден файл: " + o.FilePath);
            string ext = Path.GetExtension(o.FilePath).ToLowerInvariant();
            if (ext == ".rcp" || ext == ".rcs")
                throw new Exception("Формат .rcp/.rcs не читается напрямую. Экспортируйте из ReCap Pro в LAS (Файл → Экспорт → LAS) и повторите.");
            if (ext != ".las" && ext != ".laz" && ext != ".pts" && ext != ".ply" && ext != ".ptx" && ext != ".xyz")
                throw new Exception("Формат " + ext + " не поддерживается (нужен LAS, LAZ, PTS, PTX, PLY или XYZ).");
            if (!File.Exists(ConverterExe)) throw new Exception("Не найден PotreeConverter: " + ConverterExe);

            string title = string.IsNullOrWhiteSpace(o.Title) ? Path.GetFileNameWithoutExtension(o.FilePath) : o.Title.Trim();
            string id = Slug(title);
            say("Название: " + title);
            say("Идентификатор: " + id);

            string source = o.FilePath;

            // 1. прореживание
            if (o.Voxel > 0)
            {
                if (ext != ".las")
                {
                    say("Прореживание поддерживается только для .las — пропускаю.");
                }
                else if (!File.Exists(DecimatorPath))
                {
                    say("Нет decimate.py — прореживание пропущено.");
                }
                else
                {
                    string tempLas = Path.Combine(Path.GetTempPath(), id + "-voxel.las");
                    say(string.Format("Прореживаю сеткой {0:0} мм...", o.Voxel * 1000));
                    if (progress != null) progress(0);
                    Run("python", Quote(DecimatorPath) + " " + Quote(o.FilePath) + " " + Quote(tempLas) + " " + o.Voxel.ToString("0.####", CultureInfo.InvariantCulture),
                        Root, say, progress);
                    if (!File.Exists(tempLas)) throw new Exception("Прореживание не создало файл");
                    FileInfo fi = new FileInfo(tempLas);
                    say(string.Format("После прореживания: {0:0.0} МБ", fi.Length / 1048576.0));
                    source = tempLas;
                }
            }

            // 2. конвертация
            string outDir = Path.Combine(Root, "data", id);
            if (Directory.Exists(outDir)) say("Папка облака существует — перезапишу.");
            string format = string.Equals(o.Profile, "hq", StringComparison.OrdinalIgnoreCase) ? "BINARY" : "LAZ";
            say("Конвертация в Potree (" + format + ")...");
            if (progress != null) progress(0);
            Run(ConverterExe,
                Quote(source) + " -o " + Quote(outDir) + " --overwrite --output-format " + format + " -a RGB",
                Root, say, progress);

            string cloudJs = Path.Combine(outDir, "cloud.js");
            if (!File.Exists(cloudJs)) throw new Exception("Конвертация не дала результата: " + cloudJs);

            // превью для галереи (пока исходный LAS ещё на месте)
            string poster = null;
            say("Готовлю превью...");
            if (MakePoster(source, id, say)) poster = "data/" + id + "/poster.png";
            else say("Превью пропущено — карточка будет с заглушкой.");

            if (source != o.FilePath && File.Exists(source)) { try { File.Delete(source); } catch (Exception) { } }

            long sizeBytes = 0;
            try
            {
                foreach (FileInfo f in new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories))
                    sizeBytes += f.Length;
            }
            catch (Exception) { }
            int points = ReadPoints(cloudJs);
            say(string.Format("Готово: {0:N0} точек, {1:0.0} МБ", points, sizeBytes / 1048576.0));

            // 3. clouds.json
            string src = "data/" + id + "/cloud.js";
            WriteCloudsJson(id, title, o.Description, points, src, poster);
            say("clouds.json обновлён.");

            // 4. git
            if (o.Push)
            {
                // применяем выбранное подключение, если настроены owner/repo
                EnsureConfiguredRemote(say);

                say("Коммит и отправка на GitHub...");
                Run("git", "add -- .gitignore clouds.json " + Quote("data/" + id), Root, say, null);
                RunCapture("git", "diff --cached --quiet", Root, null, null); // если нечего коммитить — не ошибка
                string commit = RunCapture("git", "commit -m " + Quote("add pointcloud: " + title), Root, say, null);
                if (commit.Contains("exit=0")) say("Коммит создан."); else say("Новых изменений для коммита нет.");

                string remotes = RunCapture("git", "remote", Root, null, null);
                if (!remotes.Contains("origin")) throw new Exception("У репозитория нет remote origin. Добавьте: git remote add origin https://github.com/<owner>/<repo>.git");
                string push = RunCapture("git", "push", Root, say, null);
                if (!push.Contains("|exit=0"))
                    throw new Exception("Не удалось отправить на GitHub. Проверьте доступ/сеть и повторите. Подробности в журнале.");
                say("Отправлено на GitHub.");
            }

            // 5. ссылка
            string link = BuildLink(src, title);
            say("Ссылка: " + link);
            if (o.OpenInBrowser) TryOpen(link);
            return link;
        }

        private static long Sum(this FileSystemInfo[] files) { return 0; }

        private static int ReadPoints(string cloudJs)
        {
            try
            {
                string text = File.ReadAllText(cloudJs, Encoding.UTF8);
                Match m = Regex.Match(text, "\"points\"\\s*:\\s*(\\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            catch (Exception) { }
            return 0;
        }

        internal static string JsonEscape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        internal static string JsonField(string obj, string name)
        {
            Match m = Regex.Match(obj, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            try { return Regex.Unescape(m.Groups[1].Value); } catch (Exception) { return m.Groups[1].Value; }
        }

        internal static string IntField(string obj, string name)
        {
            Match m = Regex.Match(obj, "\"" + name + "\"\\s*:\\s*(\\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        internal static void WriteEntry(StringBuilder sb, string id, string title, string description,
            string src, string points, string date, string poster, string rcp)
        {
            List<string> lines = new List<string>();
            lines.Add("    \"id\": \"" + JsonEscape(id ?? "") + "\"");
            lines.Add("    \"title\": \"" + JsonEscape(title ?? "") + "\"");
            lines.Add("    \"description\": \"" + JsonEscape(description ?? "") + "\"");
            lines.Add("    \"src\": \"" + JsonEscape(src ?? "") + "\"");
            if (!string.IsNullOrEmpty(points)) lines.Add("    \"points\": " + points);
            if (!string.IsNullOrEmpty(date)) lines.Add("    \"date\": \"" + JsonEscape(date) + "\"");
            if (!string.IsNullOrEmpty(poster)) lines.Add("    \"poster\": \"" + JsonEscape(poster) + "\"");
            if (!string.IsNullOrEmpty(rcp)) lines.Add("    \"rcp\": \"" + JsonEscape(rcp) + "\"");
            sb.Append("  {\r\n").Append(string.Join(",\r\n", lines.ToArray())).Append("\r\n  }");
        }

        private static void WriteCloudsJson(string id, string title, string description, int points, string src, string poster)
        {
            List<CloudEntry> entries = CloudEntry.LoadAll();
            List<CloudEntry> rest = new List<CloudEntry>();
            foreach (CloudEntry e in entries)
                if (e.Id != id) rest.Add(e);

            CloudEntry entry = new CloudEntry();
            entry.Id = id;
            entry.Title = title;
            entry.Description = description;
            entry.Src = src;
            entry.Points = points > 0 ? points.ToString() : "";
            entry.Date = DateTime.Now.ToString("yyyy-MM-dd");
            entry.Poster = poster ?? "";
            rest.Insert(0, entry);

            CloudEntry.SaveAll(rest);
        }

        public static void UpdateCloud(string id, string title, string description, string poster, string rcp)
        {
            List<CloudEntry> entries = CloudEntry.LoadAll();
            bool found = false;
            foreach (CloudEntry e in entries)
            {
                if (e.Id != id) continue;
                e.Title = title;
                e.Description = description;
                if (poster != null) e.Poster = poster;
                if (rcp != null) e.Rcp = rcp;
                found = true;
            }
            if (!found) throw new Exception("Запись не найдена в clouds.json: " + id);
            CloudEntry.SaveAll(entries);
        }

        public static void DeleteCloud(string id, bool deleteFiles)
        {
            List<CloudEntry> entries = CloudEntry.LoadAll();
            List<CloudEntry> rest = new List<CloudEntry>();
            foreach (CloudEntry e in entries)
                if (e.Id != id) rest.Add(e);
            CloudEntry.SaveAll(rest);

            if (deleteFiles)
            {
                string dir = Path.Combine(Root, "data", id);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        public static string PosterPathFor(string id)
        {
            return Path.Combine(Root, "data", id, "poster.png");
        }

        // Рендер превью из LAS через thumbnail.py. Возвращает true, если PNG создан.
        public static bool MakePoster(string lasPath, string id, Action<string> log)
        {
            string script = Path.Combine(Root, "thumbnail.py");
            if (!File.Exists(script))
            {
                if (log != null) log("Нет thumbnail.py — превью не создано.");
                return false;
            }
            if (!File.Exists(lasPath))
            {
                if (log != null) log("Нет файла LAS для превью: " + lasPath);
                return false;
            }
            string dir = Path.Combine(Root, "data", id);
            try { Directory.CreateDirectory(dir); }
            catch (Exception) { }

            string target = Path.Combine(dir, "poster.png");
            try
            {
                Run("python", Quote(script) + " " + Quote(lasPath) + " " + Quote(target), Root, log, null);
            }
            catch (Exception ex)
            {
                if (log != null) log("Превью не создано: " + ex.Message);
                return false;
            }
            return File.Exists(target);
        }

        // Гарантирует, что origin соответствует выбранным в настройках owner/repo.
        public static void EnsureConfiguredRemote(Action<string> log)
        {
            GithubSettings gs = GithubSettings.Load();
            if (!gs.IsSet) return;
            try
            {
                string current = RunCapture("git", "remote get-url origin", Root, null, null).Trim();
                string expected = string.IsNullOrEmpty(gs.Token)
                    ? "https://github.com/" + gs.Owner + "/" + gs.Repo + ".git"
                    : "https://" + gs.Owner + ":" + gs.Token + "@github.com/" + gs.Owner + "/" + gs.Repo + ".git";
                if (!current.Equals(expected, StringComparison.Ordinal))
                {
                    if (log != null) log("Настраиваю remote origin под выбранный аккаунт...");
                    ConfigureRemote(gs, log);
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("remote origin: " + ex.Message);
            }
        }

        public static void GitCommitPush(string message, string[] paths, Action<string> log)
        {
            Action<string> say = delegate(string s) { if (log != null) log(s); };
            EnsureConfiguredRemote(log);

            StringBuilder add = new StringBuilder("add -A --");
            foreach (string p in paths) add.Append(" ").Append(Quote(p));
            Run("git", add.ToString(), Root, say, null);

            RunCapture("git", "diff --cached --quiet", Root, null, null);
            string commit = RunCapture("git", "commit -m " + Quote(message), Root, say, null);
            if (!commit.Contains("exit=0"))
            {
                say("Нет изменений для коммита.");
                return;
            }
            say("Коммит создан.");
            string push = RunCapture("git", "push", Root, say, null);
            if (!push.Contains("|exit=0"))
                throw new Exception("Не удалось отправить изменения на GitHub. Подробности в журнале.");
            say("Изменения отправлены на GitHub.");
        }

        public static string BuildLink(string src, string title)
        {
            GithubSettings s = GithubSettings.Load();
            if (s.IsSet)
            {
                string abs = s.PagesBase + "/" + src;
                return s.PagesBase + "/website/index.html#cloud=" +
                    Uri.EscapeDataString(abs) + "&name=" + Uri.EscapeDataString(title ?? "");
            }

            string remote = "";
            try
            {
                string res = RunCapture("git", "remote get-url origin", Root, null, null);
                remote = res;
            }
            catch (Exception) { }

            Match m = Regex.Match(remote, @"github\.com[:/](?<o>[^/\s]+)/(?<r>[^/\s\.]+)");
            if (!m.Success) return "website/index.html#cloud=" + Uri.EscapeDataString(src) + "&name=" + Uri.EscapeDataString(title ?? "");

            string owner = m.Groups["o"].Value;
            string repo = m.Groups["r"].Value;
            string absUrl = "https://" + owner + ".github.io/" + repo + "/" + src;
            return "https://" + owner + ".github.io/" + repo + "/website/index.html#cloud=" +
                Uri.EscapeDataString(absUrl) + "&name=" + Uri.EscapeDataString(title ?? "");
        }

        // Настраивает origin под выбранные owner/repo/token и возвращает сообщение о состоянии.
        public static string ConfigureRemote(GithubSettings s, Action<string> log)
        {
            try
            {
                RunCapture("git", "remote remove origin", Root, log, null);
            }
            catch (Exception) { }
            string url = string.IsNullOrEmpty(s.Token)
                ? "https://github.com/" + s.Owner + "/" + s.Repo + ".git"
                : "https://" + s.Owner + ":" + s.Token + "@github.com/" + s.Owner + "/" + s.Repo + ".git";
            RunCapture("git", "remote add origin " + Quote(url), Root, log, null);
            return url;
        }

        // Проверка токена и репозитория через GitHub API. Возвращает (owner, repo) то, что реально доступно.
        public static string VerifyGithub(GithubSettings s, Action<string> log)
        {
            string baseUrl = "https://api.github.com";
            using (WebClient wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "ScanPublisher";
                wc.Headers[HttpRequestHeader.Authorization] = "token " + s.Token;
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                try
                {
                    string user = wc.DownloadString(baseUrl + "/user");
                    string login = JsonField(user, "login");
                    if (string.IsNullOrEmpty(login)) throw new Exception("Не удалось определить логин по токену.");
                    if (log != null) log("Токен действителен, аккаунт: " + login);
                    if (string.IsNullOrEmpty(s.Owner)) s.Owner = login;
                }
                catch (WebException ex)
                {
                    HttpWebResponse r = ex.Response as HttpWebResponse;
                    if (r != null && (int)r.StatusCode == 401) throw new Exception("Неверный токен (401). Проверьте токен на https://github.com/settings/tokens.");
                    if (r != null && (int)r.StatusCode == 403) throw new Exception("Доступ запрещён (403). Возможно, истёкли права токена. Нужен scope: repo (или fine-grained: Contents read/write, Metadata read).");
                    throw new Exception("Ошибка подключения к GitHub API: " + ex.Message);
                }

                // проверяем доступ к репозиторию
                try
                {
                    string repo = wc.DownloadString(baseUrl + "/repos/" + s.Owner + "/" + s.Repo);
                    string full = JsonField(repo, "full_name");
                    if (log != null) log("Репозиторий доступен: " + (full ?? (s.Owner + "/" + s.Repo)));
                }
                catch (WebException ex)
                {
                    HttpWebResponse r = ex.Response as HttpWebResponse;
                    int code = r != null ? (int)r.StatusCode : 0;
                    if (code == 404) throw new Exception("Репозиторий " + s.Owner + "/" + s.Repo + " не найден или у токена нет к нему доступа (404).");
                    if (code == 401 || code == 403) throw new Exception("Нет прав на репозиторий " + s.Owner + "/" + s.Repo + " (" + code + "). Проверьте права токена и Owner/Repo.");
                    throw new Exception("Ошибка проверки репозитория: " + ex.Message);
                }
                return s.Owner + "/" + s.Repo;
            }
        }

        public static void TryOpen(string link)
        {
            try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
            catch (Exception) { }
        }

        private static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

        // ---------- CLI ----------
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        public static int RunCli(string[] args)
        {
            AttachConsole(-1);
            PublishOptions o = new PublishOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : "";
                if (a == "--file") { o.FilePath = next; i++; }
                else if (a == "--title") { o.Title = next; i++; }
                else if (a == "--desc") { o.Description = next; i++; }
                else if (a == "--voxel") { double v; if (double.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) o.Voxel = v; i++; }
                else if (a == "--profile") { o.Profile = next; i++; }
                else if (a == "--no-push") { o.Push = false; }
                else if (a == "--open") { o.OpenInBrowser = true; }
            }

            if (string.IsNullOrEmpty(o.FilePath))
            {
                Console.WriteLine("Использование: ScanPublisher.exe --cli --file <путь.las> [--title \"Имя\"] [--desc \"...\"] [--voxel 0.01] [--profile web|hq] [--no-push] [--open]");
                return 2;
            }

            try
            {
                string link = Publish(o, delegate(string s) { Console.WriteLine(s); }, null);
                Console.WriteLine("ГОТОВО: " + link);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ОШИБКА: " + ex.Message);
                return 1;
            }
        }
    }

    internal class MainForm : Form
    {
        private readonly TextBox txtFile = new TextBox();
        private readonly TextBox txtTitle = new TextBox();
        private readonly TextBox txtDesc = new TextBox();
        private readonly ComboBox cbVoxel = new ComboBox();
        private readonly ComboBox cbProfile = new ComboBox();
        private readonly CheckBox chkOpen = new CheckBox();
        private readonly Button btnPublish = new Button();
        private readonly TextBox txtLog = new TextBox();
        private readonly TextBox txtLink = new TextBox();
        private readonly Label lblStatus = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private bool busy;

        public MainForm()
        {
            Text = "ScanPublisher — публикация облаков точек";
            ClientSize = new Size(980, 620);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            AllowDrop = true;

            GroupBox gbFile = new GroupBox();
            gbFile.Text = "Скан (перетащите .las/.laz в окно)";
            gbFile.SetBounds(12, 8, 956, 58);
            gbFile.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            txtFile.SetBounds(12, 24, 700, 23);
            txtFile.ReadOnly = true;
            txtFile.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gbFile.Controls.Add(txtFile);

            Button btnBrowse = new Button();
            btnBrowse.Text = "Выбрать...";
            btnBrowse.SetBounds(722, 23, 100, 25);
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Click += delegate { BrowseFile(); };
            gbFile.Controls.Add(btnBrowse);
            Controls.Add(gbFile);

            GroupBox gbInfo = new GroupBox();
            gbInfo.Text = "Данные облака";
            gbInfo.SetBounds(12, 72, 620, 148);
            gbInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Label l1 = new Label(); l1.Text = "Название"; l1.SetBounds(12, 22, 90, 18); gbInfo.Controls.Add(l1);
            txtTitle.SetBounds(12, 42, 594, 23);
            txtTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtTitle.TextChanged += delegate { if (!string.IsNullOrEmpty(txtFile.Text) && txtTitle.Tag == null) TuneIdPreview(); };
            gbInfo.Controls.Add(txtTitle);

            Label l2 = new Label(); l2.Text = "Описание"; l2.SetBounds(12, 68, 90, 18); gbInfo.Controls.Add(l2);
            txtDesc.SetBounds(12, 88, 594, 23);
            txtDesc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gbInfo.Controls.Add(txtDesc);

            Label l3 = new Label(); l3.Text = "Прореживание"; l3.SetBounds(12, 116, 110, 18); gbInfo.Controls.Add(l3);
            cbVoxel.DropDownStyle = ComboBoxStyle.DropDownList;
            cbVoxel.SetBounds(126, 113, 160, 23);
            cbVoxel.Items.Add("Без прореживания (полное)");
            cbVoxel.Items.Add("1 см — детально");
            cbVoxel.Items.Add("2 см — оптимум");
            cbVoxel.Items.Add("3 см — легче");
            cbVoxel.Items.Add("5 см — черновик");
            cbVoxel.SelectedIndex = 2;
            gbInfo.Controls.Add(cbVoxel);

            Label l4 = new Label(); l4.Text = "Формат"; l4.SetBounds(420, 116, 60, 18); gbInfo.Controls.Add(l4);
            cbProfile.DropDownStyle = ComboBoxStyle.DropDownList;
            cbProfile.SetBounds(480, 113, 126, 23);
            cbProfile.Items.Add("web (LAZ, сжато)");
            cbProfile.Items.Add("hq (без сжатия)");
            cbProfile.SelectedIndex = 0;
            gbInfo.Controls.Add(cbProfile);
            Controls.Add(gbInfo);

            GroupBox gbAction = new GroupBox();
            gbAction.Text = "Публикация";
            gbAction.SetBounds(640, 72, 328, 148);
            gbAction.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            chkOpen.Text = "Открыть в браузере";
            chkOpen.SetBounds(14, 22, 220, 20);
            chkOpen.Checked = true;
            gbAction.Controls.Add(chkOpen);

            btnPublish.Text = "Опубликовать";
            btnPublish.SetBounds(14, 48, 300, 40);
            btnPublish.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnPublish.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnPublish.Click += delegate { StartPublish(); };
            gbAction.Controls.Add(btnPublish);

            progress.SetBounds(14, 96, 300, 14);
            progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progress.Minimum = 0; progress.Maximum = 100;
            gbAction.Controls.Add(progress);

            lblStatus.Text = "Готово к работе";
            lblStatus.SetBounds(14, 116, 300, 18);
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblStatus.ForeColor = Color.DimGray;
            gbAction.Controls.Add(lblStatus);
            Controls.Add(gbAction);

            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.FromArgb(250, 250, 250);
            txtLog.Font = new Font("Consolas", 8.5F);
            txtLog.SetBounds(12, 228, 956, 318);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(txtLog);

            Label l5 = new Label(); l5.Text = "Ссылка"; l5.SetBounds(12, 554, 50, 18);
            l5.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(l5);

            txtLink.SetBounds(66, 551, 510, 23);
            txtLink.ReadOnly = true;
            txtLink.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(txtLink);

            Button btnLibrary = new Button();
            btnLibrary.Text = "Библиотека";
            btnLibrary.SetBounds(584, 550, 96, 25);
            btnLibrary.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnLibrary.Click += delegate { ShowLibrary(); };
            Controls.Add(btnLibrary);

            Button btnSettings = new Button();
            btnSettings.Text = "Настройки GitHub";
            btnSettings.SetBounds(688, 550, 96, 25);
            btnSettings.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnSettings.Click += delegate { ShowSettings(); };
            Controls.Add(btnSettings);

            Button btnOpen = new Button();
            btnOpen.Text = "Открыть";
            btnOpen.SetBounds(792, 550, 60, 25);
            btnOpen.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOpen.Click += delegate { if (txtLink.Text.Length > 0) Pipeline.TryOpen(txtLink.Text); };
            Controls.Add(btnOpen);

            Button btnCopy = new Button();
            btnCopy.Text = "Копировать";
            btnCopy.SetBounds(860, 550, 100, 25);
            btnCopy.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCopy.Click += delegate
            {
                if (txtLink.Text.Length == 0) return;
                try { Clipboard.SetText(txtLink.Text); lblStatus.Text = "Ссылка скопирована"; }
                catch (Exception) { }
            };
            Controls.Add(btnCopy);

            DragEnter += delegate(object sender, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            DragDrop += delegate(object sender, DragEventArgs e)
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) SetFile(files[0]);
            };
        }

        private void TuneIdPreview()
        {
            if (string.IsNullOrWhiteSpace(txtTitle.Text)) txtTitle.Tag = null;
        }

        private void BrowseFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Выберите облако точек";
                d.Filter = "Облака точек|*.las;*.laz;*.pts;*.ptx;*.ply;*.xyz|Все файлы|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) SetFile(d.FileName);
            }
        }

        private void SetFile(string path)
        {
            if (Directory.Exists(path))
            {
                MessageBox.Show(this, "Нужен файл облака (.las/.laz), а не папка.", "ScanPublisher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            txtFile.Text = path;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".rcp" || ext == ".rcs")
            {
                MessageBox.Show(this,
                    "Файл .rcp/.rcs напрямую не читается (закрытый формат Autodesk).\r\n\r\n" +
                    "В ReCap Pro: Файл → Экспорт → LAS, затем выберите полученный .las.",
                    "Нужен экспорт из ReCap", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (txtTitle.Tag == null && string.IsNullOrWhiteSpace(txtTitle.Text))
                txtTitle.Text = Path.GetFileNameWithoutExtension(path);
            txtTitle.Tag = null;
            lblStatus.Text = "Файл выбран: " + Path.GetFileName(path);
        }

        private double SelectedVoxel()
        {
            switch (cbVoxel.SelectedIndex)
            {
                case 1: return 0.01;
                case 2: return 0.02;
                case 3: return 0.03;
                case 4: return 0.05;
                default: return 0;
            }
        }

        private void StartPublish()
        {
            if (busy) return;
            if (string.IsNullOrWhiteSpace(txtFile.Text) || !File.Exists(txtFile.Text))
            {
                MessageBox.Show(this, "Сначала выберите файл скана.", "ScanPublisher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            PublishOptions o = new PublishOptions();
            o.FilePath = txtFile.Text;
            o.Title = txtTitle.Text;
            o.Description = txtDesc.Text;
            o.Voxel = SelectedVoxel();
            o.Profile = cbProfile.SelectedIndex == 1 ? "hq" : "web";
            o.Push = true;
            o.OpenInBrowser = false;

            busy = true;
            btnPublish.Enabled = false;
            progress.Value = 0;
            txtLog.Clear();
            txtLink.Clear();
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Text = "Работаю...";

            Thread t = new Thread(delegate()
            {
                try
                {
                    string link = Pipeline.Publish(o,
                        delegate(string s) { AppendLog(s); },
                        delegate(int p) { SetProgress(p); });

                    Invoke((MethodInvoker)delegate
                    {
                        txtLink.Text = link;
                        lblStatus.ForeColor = Color.SeaGreen;
                        lblStatus.Text = "Опубликовано";
                        progress.Value = 100;
                        if (chkOpen.Checked) Pipeline.TryOpen(link);
                    });
                }
                catch (Exception ex)
                {
                    AppendLog("");
                    AppendLog("ОШИБКА: " + ex.Message);
                    Invoke((MethodInvoker)delegate
                    {
                        lblStatus.ForeColor = Color.Firebrick;
                        lblStatus.Text = "Ошибка";
                    });
                }
                finally
                {
                    Invoke((MethodInvoker)delegate
                    {
                        busy = false;
                        btnPublish.Enabled = true;
                        progress.Value = 0;
                    });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AppendLog(string line)
        {
            if (txtLog.IsDisposed) return;
            if (txtLog.InvokeRequired)
            {
                txtLog.BeginInvoke((MethodInvoker)delegate { AppendLog(line); });
                return;
            }
            txtLog.AppendText(line + "\r\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        private void SetProgress(int value)
        {
            if (progress.IsDisposed) return;
            if (progress.InvokeRequired)
            {
                progress.BeginInvoke((MethodInvoker)delegate { SetProgress(value); });
                return;
            }
            if (value < progress.Minimum) value = progress.Minimum;
            if (value > progress.Maximum) value = progress.Maximum;
            progress.Value = value;
        }

        private void ShowSettings()
        {
            if (busy) { MessageBox.Show(this, "Дождитесь завершения публикации.", "ScanPublisher", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (SettingsForm f = new SettingsForm())
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    string ok = f.SavedRepo;
                    if (ok != null)
                        lblStatus.Text = "Подключено: " + ok;
                }
            }
        }

        private void ShowLibrary()
        {
            if (busy) { MessageBox.Show(this, "Дождитесь завершения публикации.", "ScanPublisher", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (LibraryForm f = new LibraryForm())
            {
                f.ShowDialog(this);
            }
        }
    }

    internal class SettingsForm : Form
    {
        private readonly TextBox txtOwner = new TextBox();
        private readonly TextBox txtRepo = new TextBox();
        private readonly TextBox txtBranch = new TextBox();
        private readonly TextBox txtToken = new TextBox();
        private readonly Label lblState = new Label();
        private readonly Button btnVerify = new Button();
        private readonly Button btnSave = new Button();
        private readonly Button btnCancel = new Button();
        private GithubSettings settings;

        public string SavedRepo { get; private set; }

        public SettingsForm()
        {
            Text = "Настройки GitHub";
            ClientSize = new Size(520, 330);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F);

            settings = GithubSettings.Load();

            Label l0 = new Label(); l0.Text = "Подключение к GitHub для публикации облаков. Ссылки выдаются как <owner>.github.io/<repo>."; l0.SetBounds(12, 10, 496, 30); Controls.Add(l0);

            Label l1 = new Label(); l1.Text = "Владелец (account)"; l1.SetBounds(12, 48, 180, 18); Controls.Add(l1);
            txtOwner.SetBounds(12, 68, 496, 23); txtOwner.Text = settings.Owner ?? ""; Controls.Add(txtOwner);

            Label l2 = new Label(); l2.Text = "Репозиторий"; l2.SetBounds(12, 96, 180, 18); Controls.Add(l2);
            txtRepo.SetBounds(12, 116, 496, 23); txtRepo.Text = settings.Repo ?? ""; Controls.Add(txtRepo);

            Label l3 = new Label(); l3.Text = "Ветка"; l3.SetBounds(12, 144, 180, 18); Controls.Add(l3);
            txtBranch.SetBounds(12, 164, 110, 23); txtBranch.Text = settings.Branch ?? ""; Controls.Add(txtBranch);

            Label l4 = new Label(); l4.Text = "Токен (личный PAT)"; l4.SetBounds(130, 144, 200, 18); Controls.Add(l4);
            txtToken.SetBounds(130, 164, 378, 23); txtToken.UseSystemPasswordChar = true; txtToken.Text = settings.Token ?? ""; Controls.Add(txtToken);

            lblState.Text = "Заполните и нажмите «Проверить».";
            lblState.SetBounds(12, 198, 496, 30);
            lblState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lblState);

            btnVerify.Text = "Проверить подключение";
            btnVerify.SetBounds(12, 248, 200, 28);
            btnVerify.Click += delegate { Verify(); };
            Controls.Add(btnVerify);

            btnSave.Text = "Сохранить";
            btnSave.SetBounds(320, 248, 84, 28);
            btnSave.DialogResult = DialogResult.OK;
            btnSave.Click += delegate { SaveAndClose(); };
            Controls.Add(btnSave);

            btnCancel.Text = "Отмена";
            btnCancel.SetBounds(410, 248, 84, 28);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = btnVerify;
        }

        private GithubSettings Collect()
        {
            GithubSettings s = new GithubSettings();
            s.Owner = txtOwner.Text.Trim();
            s.Repo = txtRepo.Text.Trim();
            s.Branch = txtBranch.Text.Trim();
            if (s.Branch.Length == 0) s.Branch = "main";
            s.Token = txtToken.Text.Trim();
            return s;
        }

        private void Verify()
        {
            GithubSettings s = Collect();
            if (string.IsNullOrEmpty(s.Owner) || string.IsNullOrEmpty(s.Repo))
            {
                lblState.Text = "Заполните владельца и репозиторий.";
                lblState.ForeColor = Color.Firebrick;
                return;
            }
            if (string.IsNullOrEmpty(s.Token))
            {
                lblState.Text = "Укажите токен для проверки (без токена проверить нельзя).";
                lblState.ForeColor = Color.Firebrick;
                return;
            }
            btnVerify.Enabled = false;
            lblState.Text = "Проверяю...";
            lblState.ForeColor = Color.DimGray;
            btnVerify.Text = "Проверяю...";
            try
            {
                string ok = Pipeline.VerifyGithub(s, null);
                lblState.Text = "✓ Подключение работает: " + ok;
                lblState.ForeColor = Color.SeaGreen;
                settings = s;
            }
            catch (Exception ex)
            {
                lblState.Text = "✗ " + ex.Message;
                lblState.ForeColor = Color.Firebrick;
            }
            finally
            {
                btnVerify.Enabled = true;
                btnVerify.Text = "Проверить подключение";
            }
        }

        private void SaveAndClose()
        {
            GithubSettings s = Collect();
            if (string.IsNullOrEmpty(s.Owner) || string.IsNullOrEmpty(s.Repo))
            {
                MessageBox.Show(this, "Укажите владельца и репозиторий.", "Настройки", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
                return;
            }
            s.Save();
            SavedRepo = s.RepoDisplay;
            DialogResult = DialogResult.OK;
        }
    }

    internal class LibraryForm : Form
    {
        private readonly ListView lv = new ListView();
        private readonly ImageList thumbs = new ImageList();
        private readonly PictureBox preview = new PictureBox();
        private readonly TextBox txtTitle = new TextBox();
        private readonly TextBox txtDesc = new TextBox();
        private readonly Label lblMeta = new Label();
        private readonly Label lblState = new Label();
        private readonly Button btnSave = new Button();
        private readonly Button btnDelete = new Button();
        private readonly Button btnPoster = new Button();
        private readonly Button btnOpen = new Button();
        private readonly Button btnCopy = new Button();
        private readonly Button btnRefresh = new Button();
        private readonly Button btnClose = new Button();
        private List<CloudEntry> entries = new List<CloudEntry>();
        private bool busy;

        public LibraryForm()
        {
            Text = "Библиотека облаков";
            ClientSize = new Size(1000, 560);
            MinimumSize = new Size(920, 520);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);

            lv.SetBounds(12, 12, 460, 500);
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.MultiSelect = false;
            lv.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            lv.Columns.Add("Название", 250);
            lv.Columns.Add("Точек", 80, HorizontalAlignment.Right);
            lv.Columns.Add("Дата", 72);
            thumbs.ImageSize = new Size(56, 35);
            thumbs.ColorDepth = ColorDepth.Depth24Bit;
            lv.SmallImageList = thumbs;
            lv.SelectedIndexChanged += delegate { ShowSelected(); };
            lv.DoubleClick += delegate { OpenSelected(); };
            Controls.Add(lv);

            preview.SetBounds(488, 12, 500, 250);
            preview.SizeMode = PictureBoxSizeMode.Zoom;
            preview.BackColor = Color.FromArgb(20, 24, 30);
            preview.BorderStyle = BorderStyle.FixedSingle;
            preview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(preview);

            Label l1 = new Label(); l1.Text = "Название"; l1.SetBounds(488, 272, 90, 18);
            l1.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(l1);

            txtTitle.SetBounds(488, 292, 500, 23);
            txtTitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(txtTitle);

            Label l2 = new Label(); l2.Text = "Описание"; l2.SetBounds(488, 320, 90, 18);
            l2.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(l2);

            txtDesc.Multiline = true;
            txtDesc.ScrollBars = ScrollBars.Vertical;
            txtDesc.SetBounds(488, 340, 500, 56);
            txtDesc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(txtDesc);

            lblMeta.SetBounds(488, 402, 500, 32);
            lblMeta.ForeColor = Color.DimGray;
            lblMeta.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lblMeta);

            btnSave.Text = "Сохранить";
            btnSave.SetBounds(488, 436, 110, 28);
            btnSave.Click += delegate { SaveEntry(); };
            Controls.Add(btnSave);

            btnOpen.Text = "Открыть";
            btnOpen.SetBounds(604, 436, 90, 28);
            btnOpen.Click += delegate { OpenSelected(); };
            Controls.Add(btnOpen);

            btnCopy.Text = "Ссылка";
            btnCopy.SetBounds(700, 436, 90, 28);
            btnCopy.Click += delegate { CopyLink(); };
            Controls.Add(btnCopy);

            btnPoster.Text = "Превью из LAS…";
            btnPoster.SetBounds(796, 436, 192, 28);
            btnPoster.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnPoster.Click += delegate { MakePosterForSelected(); };
            Controls.Add(btnPoster);

            btnDelete.Text = "Удалить";
            btnDelete.SetBounds(488, 470, 110, 28);
            btnDelete.ForeColor = Color.Firebrick;
            btnDelete.Click += delegate { DeleteSelected(); };
            Controls.Add(btnDelete);

            btnRefresh.Text = "Обновить";
            btnRefresh.SetBounds(604, 470, 90, 28);
            btnRefresh.Click += delegate { LoadList(null); };
            Controls.Add(btnRefresh);

            btnClose.Text = "Закрыть";
            btnClose.SetBounds(888, 470, 100, 28);
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            lblState.SetBounds(488, 506, 500, 40);
            lblState.ForeColor = Color.DimGray;
            lblState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(lblState);

            LoadList(null);
        }

        private void LoadList(string keepId)
        {
            entries = CloudEntry.LoadAll();
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                thumbs.Images.Clear();
                foreach (CloudEntry e in entries)
                {
                    ListViewItem item = new ListViewItem(string.IsNullOrEmpty(e.Title) ? e.Id : e.Title);
                    item.SubItems.Add(e.PointsValue > 0 ? e.PointsValue.ToString("N0") : "—");
                    item.SubItems.Add(string.IsNullOrEmpty(e.Date) ? "—" : e.Date);
                    item.Tag = e;
                    item.ImageIndex = thumbs.Images.Count;
                    thumbs.Images.Add(MakeThumb(e));
                    lv.Items.Add(item);
                }
            }
            finally { lv.EndUpdate(); }

            if (lv.Items.Count > 0)
            {
                int idx = 0;
                if (!string.IsNullOrEmpty(keepId))
                    for (int i = 0; i < lv.Items.Count; i++)
                        if (((CloudEntry)lv.Items[i].Tag).Id == keepId) { idx = i; break; }
                lv.Items[idx].Selected = true;
                lv.Items[idx].Focused = true;
                ShowSelected();
            }
            else
            {
                preview.Image = null;
                txtTitle.Text = "";
                txtDesc.Text = "";
                lblMeta.Text = "";
                lblState.Text = "Библиотека пуста. Опубликуйте первое облако.";
                SetButtons(false);
            }
            SetBusy(busy);
        }

        private Image MakeThumb(CloudEntry e)
        {
            string path = Pipeline.PosterPathFor(e.Id);
            Image img = LoadImageFile(path);
            if (img != null) return img;

            Bitmap bmp = new Bitmap(56, 35);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(20, 24, 30));
                string letter = string.IsNullOrEmpty(e.Title) ? "·" : e.Title.Substring(0, 1).ToUpperInvariant();
                using (Font f = new Font("Segoe UI", 14F, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(122, 167, 208)))
                {
                    SizeF size = g.MeasureString(letter, f);
                    g.DrawString(letter, f, b, (56 - size.Width) / 2f, (35 - size.Height) / 2f);
                }
            }
            return bmp;
        }

        private Image MakeBigPlaceholder(CloudEntry e)
        {
            Bitmap bmp = new Bitmap(500, 250);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(20, 24, 30));
                string letter = string.IsNullOrEmpty(e.Title) ? "·" : e.Title.Substring(0, 1).ToUpperInvariant();
                using (Font big = new Font("Segoe UI", 54F, FontStyle.Bold))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(60, 84, 108)))
                {
                    SizeF s = g.MeasureString(letter, big);
                    g.DrawString(letter, big, brush, (500 - s.Width) / 2f, (250 - s.Height) / 2f - 24);
                }
                using (Font small = new Font("Segoe UI", 9F))
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(120, 140, 160)))
                {
                    const string hint = "Превью не создано. Нажмите «Превью из LAS…»";
                    SizeF s = g.MeasureString(hint, small);
                    g.DrawString(hint, small, brush, (500 - s.Width) / 2f, 200);
                }
            }
            return bmp;
        }

        private static Image LoadImageFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (Image tmp = Image.FromStream(fs))
                    return new Bitmap(tmp);
            }
            catch (Exception) { return null; }
        }

        private CloudEntry SelectedEntry
        {
            get
            {
                if (lv.SelectedItems.Count == 0) return null;
                return lv.SelectedItems[0].Tag as CloudEntry;
            }
        }

        private void ShowSelected()
        {
            CloudEntry e = SelectedEntry;
            if (e == null) return;
            txtTitle.Text = e.Title;
            txtDesc.Text = e.Description;

            string link = Pipeline.BuildLink(e.Src, e.Title);
            lblMeta.Text = string.Format("{0} точек · {1}\n{2}",
                e.PointsValue > 0 ? e.PointsValue.ToString("N0") : "?",
                string.IsNullOrEmpty(e.Date) ? "дата неизвестна" : e.Date,
                e.Src);

            Image img = LoadImageFile(Pipeline.PosterPathFor(e.Id));
            if (img == null) img = MakeBigPlaceholder(e);
            if (preview.Image != null && preview.Image != img)
            {
                Image old = preview.Image;
                preview.Image = null;
                try { old.Dispose(); } catch (Exception) { }
            }
            preview.Image = img;
            SetButtons(!busy);
        }

        private void SetButtons(bool enabled)
        {
            bool has = enabled && SelectedEntry != null;
            btnSave.Enabled = has;
            btnDelete.Enabled = has;
            btnPoster.Enabled = has;
            btnOpen.Enabled = has;
            btnCopy.Enabled = has;
            btnRefresh.Enabled = enabled;
            btnClose.Enabled = true;
        }

        private void SetBusy(bool value)
        {
            busy = value;
            Cursor = value ? Cursors.WaitCursor : Cursors.Default;
            lv.Enabled = !value;
            txtTitle.Enabled = !value;
            txtDesc.Enabled = !value;
            SetButtons(!value);
        }

        private void RunGit(string message, string[] paths)
        {
            SetBusy(true);
            lblState.ForeColor = Color.DimGray;
            lblState.Text = "Отправляю изменения на GitHub...";
            try
            {
                Pipeline.GitCommitPush(message, paths, delegate(string s) { lblState.Text = s; });
                lblState.ForeColor = Color.SeaGreen;
                lblState.Text = "Изменения опубликованы.";
            }
            catch (Exception ex)
            {
                lblState.ForeColor = Color.Firebrick;
                lblState.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SaveEntry()
        {
            CloudEntry e = SelectedEntry;
            if (e == null || busy) return;
            string title = txtTitle.Text.Trim();
            if (title.Length == 0)
            {
                MessageBox.Show(this, "Название не может быть пустым.", "Библиотека", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Pipeline.UpdateCloud(e.Id, title, txtDesc.Text.Trim(), null, null);
            }
            catch (Exception ex)
            {
                lblState.ForeColor = Color.Firebrick;
                lblState.Text = "Ошибка: " + ex.Message;
                return;
            }
            RunGit("update pointcloud: " + title, new string[] { "clouds.json" });
            LoadList(e.Id);
        }

        private void DeleteSelected()
        {
            CloudEntry e = SelectedEntry;
            if (e == null || busy) return;
            DialogResult r = MessageBox.Show(this,
                "Удалить облако «" + e.Title + "»?\r\n\r\n" +
                "Да — убрать из галереи и удалить файлы data\\" + e.Id + ".\r\n" +
                "Нет — убрать только из списка (файлы останутся).",
                "Удаление", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return;

            bool deleteFiles = r == DialogResult.Yes;
            try
            {
                Pipeline.DeleteCloud(e.Id, deleteFiles);
            }
            catch (Exception ex)
            {
                lblState.ForeColor = Color.Firebrick;
                lblState.Text = "Ошибка удаления: " + ex.Message;
                return;
            }
            RunGit("remove pointcloud: " + e.Title, deleteFiles
                ? new string[] { "clouds.json", "data/" + e.Id }
                : new string[] { "clouds.json" });
            LoadList(null);
        }

        private void MakePosterForSelected()
        {
            CloudEntry e = SelectedEntry;
            if (e == null || busy) return;

            string las = null;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Выберите LAS/LAZ для превью «" + e.Title + "»";
                d.Filter = "Облака точек|*.las;*.laz|Все файлы|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                las = d.FileName;
            }
            if (string.Equals(Path.GetExtension(las), ".laz", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Превью строится из .las. Распакуйте .laz в .las или укажите исходный .las.",
                    "Превью", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true);
            lblState.ForeColor = Color.DimGray;
            lblState.Text = "Строю превью...";
            bool ok = false;
            try
            {
                ok = Pipeline.MakePoster(las, e.Id, delegate(string s) { lblState.Text = s; });
            }
            catch (Exception ex)
            {
                lblState.Text = "Ошибка: " + ex.Message;
            }
            finally { SetBusy(false); }

            if (!ok)
            {
                lblState.ForeColor = Color.Firebrick;
                lblState.Text = "Превью не создано.";
                return;
            }
            try
            {
                Pipeline.UpdateCloud(e.Id, e.Title, e.Description, "data/" + e.Id + "/poster.png", null);
            }
            catch (Exception ex)
            {
                lblState.ForeColor = Color.Firebrick;
                lblState.Text = "Ошибка: " + ex.Message;
                return;
            }
            RunGit("poster for pointcloud: " + e.Title,
                new string[] { "clouds.json", "data/" + e.Id + "/poster.png" });
            LoadList(e.Id);
        }

        private void OpenSelected()
        {
            CloudEntry e = SelectedEntry;
            if (e == null) return;
            Pipeline.TryOpen(Pipeline.BuildLink(e.Src, e.Title));
        }

        private void CopyLink()
        {
            CloudEntry e = SelectedEntry;
            if (e == null) return;
            try
            {
                Clipboard.SetText(Pipeline.BuildLink(e.Src, e.Title));
                lblState.ForeColor = Color.SeaGreen;
                lblState.Text = "Ссылка скопирована.";
            }
            catch (Exception) { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
                return Pipeline.RunCli(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
