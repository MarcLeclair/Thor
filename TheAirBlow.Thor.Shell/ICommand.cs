namespace TheAirBlow.Thor.Shell; 

public interface ICommand {
    public FailInfo RunCommand(State state, List<string> args);
    public string GetDescription();
}