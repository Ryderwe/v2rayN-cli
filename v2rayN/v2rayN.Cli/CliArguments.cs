namespace v2rayN.Cli;

internal sealed class CliArguments
{
    private readonly List<string> _values;

    public CliArguments(IEnumerable<string> values)
    {
        _values = [.. values];
    }

    public int Count => _values.Count;
    public string this[int index] => _values[index];
    public IReadOnlyList<string> Values => _values;

    public bool TakeFlag(string name)
    {
        var index = _values.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }
        _values.RemoveAt(index);
        return true;
    }

    public string? TakeOption(string name)
    {
        var index = _values.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }
        if (index + 1 >= _values.Count)
        {
            throw new CliException($"{name} 需要一个参数。");
        }

        var value = _values[index + 1];
        _values.RemoveRange(index, 2);
        return value;
    }

    public string Require(int index, string description)
    {
        if (index >= _values.Count || _values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliException($"缺少{description}。");
        }
        return _values[index];
    }

    public void EnsureEmpty()
    {
        if (_values.Count > 0)
        {
            throw new CliException($"无法识别的参数: {string.Join(' ', _values)}");
        }
    }
}
