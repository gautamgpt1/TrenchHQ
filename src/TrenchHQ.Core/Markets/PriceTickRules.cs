namespace TrenchHQ.Core.Markets
{
    internal enum PriceTickState
    {
        Default,
        Up,
        Down
    }

    internal static class PriceTickRules
    {
        internal static PriceTickState GetNextState(
            bool flashEnabled,
            bool hasPreviousPrice,
            double previousPrice,
            double currentPrice,
            PriceTickState currentState)
        {
            if (!flashEnabled || !hasPreviousPrice)
            {
                return PriceTickState.Default;
            }

            if (currentPrice > previousPrice)
            {
                return PriceTickState.Up;
            }

            if (currentPrice < previousPrice)
            {
                return PriceTickState.Down;
            }

            return currentState;
        }
    }
}
