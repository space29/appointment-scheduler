using Scheduler.Domain;

namespace Scheduler.Application;

internal static class AppointmentWorkflow
{
    public static async Task<ServiceAppointment> Find(ISchedulingSession session, Guid id, bool locked = false) =>
        await session.Appointment(id, locked) ?? throw new RequestFailure(FailureType.NotFound, "NOT_FOUND", "Appointment not found.");

    public static async Task<ServiceAppointment> Lock(ISchedulingSession session, Guid id, Actor actor)
    {
        var appointment = await Find(session, id);
        actor.Owns(appointment.Snapshot.CustomerId, appointment.Snapshot.DealershipId);
        await session.LockResources(await session.ResourceIds(id));
        return await Find(session, id, true);
    }

    public static Task Events(ISchedulingSession session, ServiceAppointment appointment, IStaffDirectory staff)
    {
        var events = appointment.Events.Select(e => new OutboxEvent(e, e switch
        {
            AppointmentConfirmed => [e.Appointment.CustomerId],
            AppointmentCancelled or WorkCompleted => [e.Appointment.CustomerId, .. staff.Recipients(e.Appointment.DealershipId)],
            _ => staff.Recipients(e.Appointment.DealershipId)
        })).ToArray();
        return session.AppendEvents(events);
    }
}
