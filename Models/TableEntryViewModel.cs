public class TableEntryViewModel
{
    public string Task { get; set; }
    public string Hours { get; set; }
    public string EntryDate { get; set; }
    public List<TableEntryViewModel> Children { get; set; }
} 