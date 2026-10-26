# Manual test plan — Camunda 7 external tasks with a real Camunda client

Checks that an existing Camunda 7 worker built on the official `camunda-external-task-client-js`
runs against Fleans with only `baseUrl` changed. The stack is a real Fleans engine plus this
repo's plugin host.

Automated coverage (not repeated here): `tests/Fleans.Plugins.CamundaExternalTask.Tests`.
- REST wire format: JSON shapes, exact `application/json`, status codes, lock expiry and handover, retries, queries.
- Stream handler in an Orleans test cluster with a fake engine grain.
- A PostgreSQL variant runs with `FLEANS_PG_TESTS=1`.

## Prereqs

- A checkout of [nightBaker/fleans](https://github.com/nightBaker/fleans) at the release this repo targets (`FleansVersion` in `Directory.Build.props`), plus Docker and Node 18+.
- From fleans' `src/Fleans/`, start the engine with this repo's host attached:
  ```bash
  FLEANS_PLUGIN_HOST_PROJECT=/abs/path/fleans-plugins/src/Fleans.CamundaExternalTask.Host/Fleans.CamundaExternalTask.Host.csproj \
    dotnet run --project Fleans.Aspire
  ```
  The host runs with `appsettings.Development.json` (a SQLite file) and listens on `http://localhost:5180`.
  The engine API is at `https://localhost:7140`.
- In this folder, run `npm i camunda-external-task-client-js@3`.
- Deploy `camunda-charge.bpmn` (`POST https://localhost:7140/Definitions/deploy` with `{"BpmnXml":"…"}`, or the Web UI).

Start a fresh instance for each scenario with
`POST https://localhost:7140/Execution/start` `{"WorkflowId":"camunda-charge","Variables":{"amount":42}}`.
The `charge` activity stays **Active** until a worker runs.

## Scenario A — complete

1. `BASE_URL=http://localhost:5180/engine-rest MODE=complete node worker.js`
2. The worker logs `amount = 42 amountToCharge = 42 businessKey = null`, then `✓ completed task …`.
3. `GET /Instances/<id>/state` shows `isCompleted: true` and `paid` completed. The variables contain `charged = True` and `receipt = {"id":"R-1","total":42.5}`.
4. `GET http://localhost:5180/engine-rest/external-task/count` returns `{"count":0}`.

## Scenario B — BPMN error

`MODE=bpmnError node worker.js`. Expect: the instance is completed through `declinedBoundary` → `declined`, and `charge` has `errorState.code = "PAYMENT_DECLINED"`.

## Scenario C — failure without retries

`MODE=failure node worker.js`. Expect: `isFailed: true`. `charge` has `errorState.code = "500"`, with a message of `card service down` followed by a newline and `HTTP 503`. The host logs EventId 9363.

## Scenario D — cancelled instance

Cancel a started instance from the Web UI before running a worker. Then `MODE=complete node worker.js` receives no task, and the host logs EventId 9365 (*stale task removed*).

## Scenario E — business key from the instance business id

Start the instance with a business id:
`POST https://localhost:7140/Execution/start` `{"WorkflowId":"camunda-charge","Variables":{"amount":42},"BusinessId":"order-1001"}`.
Then `MODE=complete node worker.js`. Expect: the worker logs `businessKey = order-1001`. This checks the
engine side of the contract (the engine fills `ExecuteCustomTaskEvent.BusinessId`), which the stream-handler
tests only simulate.

Last run: 2026-10-12, scenarios A–C passed against fleans `main` (released as 0.6.0) with `camunda-external-task-client-js` 3.1.0.
