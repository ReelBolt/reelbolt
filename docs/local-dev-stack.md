# The local dev stack

The workstation stack is the compose project `reelforge` (`docker-compose.yml`), with the app on
`http://localhost:8080` and `https://localhost:8443`. It is a development environment, but it holds a
real database with real rows, and several of its failure modes are silent. This page records the ones
that have already cost a run.

## Never let a local run migrate the real dev database

`reelforge` on the `reelbolt-postgres` container is a **real** database with real projects in it.
Anything that runs the Inference API or the WorkflowEngine locally will apply pending EF migrations to
whatever database its connection string names, and it does so **at startup, before serving any
request**.

That has already gone wrong once: an agent's env file had an unquoted `;` inside the connection
string, so the shell split the command, the app fell back to the default connection string in
`appsettings.json`, and the migrations landed on `reelforge`. The schema then ran ahead of the running
containers and project creation broke there.

**If you run a service locally, create a throwaway database for it, pass the connection string as a
single properly-quoted argument, and confirm the database name before the process starts.** Do not run
migrations against `reelforge`.

## The schema has two migration histories, and they must advance together

| History | Owned by |
|---|---|
| `__EFMigrationsHistory_Api` | the Inference API |
| `__EFMigrationsHistory_Workflow` | the WorkflowEngine |

They are independent. Advancing one leaves the other behind, and the mismatch is quiet: the running
services stay healthy, and the only symptom is a single line in the Inference API's startup log —

    [startup-cleanup] failed to cancel stale executions: 42703: column w.lease_expires_at does not exist

Rebuilding **both** images is the fix. Rebuilding only the one you were working on is how you get here.

## Restarting a service can require rebuilding its image first

Several config files are bind-mounted from the repo while their surrounding logic is baked into the
image:

| Bind-mounted | Baked into the image |
|---|---|
| `nginx/nginx.conf` | `nginx/docker-entrypoint.sh` |
| `nginx/locations.conf`, templates | the compiled service binaries |

So a container can run for days against a bind-mounted config that the image it was built from never
knew about. It only notices on restart. The observed case: `nginx.conf` began including
`/etc/nginx/conf.d/realip.conf`, a file the **newer** entrypoint creates and the older one did not, so
restarting nginx with a two-day-old image crash-looped on

    [emerg] open() "/etc/nginx/conf.d/realip.conf" failed (2: No such file or directory)

**Rebuild the image before restarting the service if the repo's config has moved on.**

## The RabbitMQ queue argument trap

The engine declares `x-max-priority` on the `workflow-execution` queue. RabbitMQ treats a queue's
arguments as immutable: an engine built with the argument, meeting a queue that was created without it,
cannot start its message bus at all —

    PRECONDITION_FAILED - inequivalent arg 'x-max-priority' for queue 'workflow-execution'

The local queue holds only dev messages, so the remedy is to delete it and let the engine recreate it
with the correct argument:

    docker exec reelforge-rabbitmq-1 rabbitmqctl delete_queue workflow-execution --vhost /
    docker compose -p reelforge restart workflow-engine

On a deployment holding real work, draining that queue first is not optional.
