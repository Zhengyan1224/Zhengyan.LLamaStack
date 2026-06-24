using System.Collections.Generic;

namespace Zhengyan.ChatUI.Desktop.Models;

public class ConfigModels
{
    public int Current { get; set; }
    public List<ConfigModel> Data { get; set; } = new();
}

public class ConfigModel
{
    public string Id { get; set; } = string.Empty;
}
