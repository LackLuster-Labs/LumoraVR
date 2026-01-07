namespace Lumora.Core;

public abstract class UserComponent : ComponentBase<UserComponent>
{
    public User User { get; private set; }

    internal override void Initialize(ContainerWorker<UserComponent> container, bool isNew)
    {
        User = container as User;
        base.Initialize(container, isNew);
    }
}
