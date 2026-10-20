namespace DeliveryForge.Evidence;

// The concrete native owner supplies its existing original start/work bound.
// This can only restrict an enclosing currentness wait, never create ownership.
internal interface IOwnedRestrictedWorkCheck
{
    void CheckWork(long localStarted, TimeSpan localWork);
}
