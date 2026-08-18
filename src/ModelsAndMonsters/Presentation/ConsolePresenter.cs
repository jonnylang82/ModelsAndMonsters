using System.Text;

namespace ModelsAndMonsters.Presentation;

/// <summary>Writes the readable game transcript to stdout.</summary>
public sealed class ConsolePresenter : IGameConsole
{
    private const int FallbackWidth = 100;

    public void RunHeader(string runId, string scenarioName, string outputDirectory)
    {
        Write(ConsoleColor.DarkGray, $"Models & Monsters v0.2 — {scenarioName}");
        Write(ConsoleColor.DarkGray, $"run {runId} → {outputDirectory}");
        Console.WriteLine();
    }

    public void RoundHeader(int round)
    {
        Write(ConsoleColor.DarkGray, $"── Round {round} ──");
        Console.WriteLine();
    }

    public void DungeonMaster(string text) => Block(ConsoleColor.Cyan, "DM:", ConsoleColor.Gray, text);

    public void CharacterAsks(string characterName, string question) =>
        Block(ConsoleColor.Yellow, $"{characterName} asks:", ConsoleColor.White, Quote(question));

    public void CharacterActs(string characterName, string intent) =>
        Block(ConsoleColor.Green, $"{characterName}:", ConsoleColor.White, Quote(intent));

    public void CharacterSpeaks(string characterName, string message) =>
        Block(ConsoleColor.Blue, $"{characterName} says:", ConsoleColor.White, Quote(message));

    public void CharacterPasses(string characterName, string reason) =>
        Block(ConsoleColor.DarkGreen, $"{characterName} holds back:", ConsoleColor.Gray, Quote(reason));

    public void CharacterRefused(string characterName, string explanation) =>
        Block(ConsoleColor.Cyan, $"DM (to {characterName}):", ConsoleColor.DarkYellow, explanation);

    public void Notice(string text)
    {
        Write(ConsoleColor.DarkGray, text);
        Console.WriteLine();
    }

    public void Ending(string text)
    {
        Write(ConsoleColor.Magenta, "── The encounter ends ──");
        Console.WriteLine();
        Write(ConsoleColor.Gray, Wrap(text));
        Console.WriteLine();
    }

    private static string Quote(string text) =>
        text.StartsWith('"') ? text : $"\"{text}\"";

    private static void Block(ConsoleColor headerColour, string header, ConsoleColor bodyColour, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        Write(headerColour, header);
        Write(bodyColour, Wrap(body));
        Console.WriteLine();
    }

    private static void Write(ConsoleColor colour, string text)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = colour;
            Console.WriteLine(text);
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    private static string Wrap(string text)
    {
        var width = GetWidth();
        var builder = new StringBuilder();

        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                builder.AppendLine();
                continue;
            }

            var lineLength = 0;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (lineLength > 0 && lineLength + 1 + word.Length > width)
                {
                    builder.AppendLine();
                    lineLength = 0;
                }
                else if (lineLength > 0)
                {
                    builder.Append(' ');
                    lineLength++;
                }

                builder.Append(word);
                lineLength += word.Length;
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static int GetWidth()
    {
        try
        {
            var width = Console.WindowWidth - 2;
            return width is > 40 and < 200 ? width : FallbackWidth;
        }
        catch (IOException)
        {
            // No console attached (redirected output, test host).
            return FallbackWidth;
        }
    }
}
