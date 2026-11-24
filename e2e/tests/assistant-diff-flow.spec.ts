import { test, expect } from '@playwright/test';

// Environment variables for test credentials
const TEST_EMAIL = process.env.TEST_EMAIL;
const TEST_PASSWORD = process.env.TEST_PASSWORD;
const BASE_URL = process.env.BASE_URL || 'http://localhost';

/**
 * Helper function to create a project via API using page context
 */
async function createProject(
  apiRequest: any,
  projectName: string
): Promise<{ id: string; name: string }> {
  const response = await apiRequest.post(`${BASE_URL}/api/v1/projects`, {
    data: { name: projectName },
  });

  if (!response.ok()) {
    const errorText = await response.text();
    console.error(`Project creation debug - Status: ${response.status()}, Body: ${errorText}`);
    throw new Error(`Project creation failed: ${response.status()} ${response.statusText()}`);
  }

  const data = await response.json();
  return data;
}

/**
 * Helper function to get available agents via page context
 */
async function getAgents(apiRequest: any): Promise<Array<{ id: string; name: string }>> {
  const response = await apiRequest.get(`${BASE_URL}/api/v1/agents`);

  if (!response.ok()) {
    throw new Error(`Failed to fetch agents: ${response.status()} ${response.statusText()}`);
  }

  const data = await response.json();
  return data;
}

/**
 * Helper function to create a workflow via API using page context
 */
async function createWorkflow(
  apiRequest: any,
  projectId: string,
  workflowName: string,
  agentId: string
): Promise<{ id: string; name: string; steps: Array<any> }> {
  const response = await apiRequest.post(
    `${BASE_URL}/api/v1/projects/${projectId}/workflows`,
    {
      data: {
        name: workflowName,
        requiresUserInput: false,
        steps: [
          {
            agentDefinitionId: agentId,
            stepOrder: 1,
            label: 'Initial Step',
            stepType: 'Agent',
          },
        ],
      },
    }
  );

  if (!response.ok()) {
    throw new Error(`Workflow creation failed: ${response.status()} ${response.statusText()}`);
  }

  const data = await response.json();
  return data;
}

/**
 * Helper function to get workflow details via page context
 */
async function getWorkflow(apiRequest: any, projectId: string, workflowId: string): Promise<any> {
  const response = await apiRequest.get(
    `${BASE_URL}/api/v1/projects/${projectId}/workflows/${workflowId}`
  );

  if (!response.ok()) {
    throw new Error(`Failed to fetch workflow: ${response.status()} ${response.statusText()}`);
  }

  return response.json();
}

test.describe('Assistant Diff Preview Flow', () => {
  test.beforeEach(async ({}, testInfo) => {
    // Skip on mobile projects - desktop only test
    test.skip(testInfo.project.name === 'mobile', 'Assistant diff flow tests are desktop only');

    // Validate required environment variables
    if (!TEST_EMAIL) {
      throw new Error('TEST_EMAIL environment variable is not set');
    }
    if (!TEST_PASSWORD) {
      throw new Error('TEST_PASSWORD environment variable is not set');
    }
  });

  test('should propose, diff, and apply workflow changes via assistant', async ({ page }) => {
    // Set a longer timeout for this test (120 seconds for LLM responses)
    test.setTimeout(120000);

    // Step 1: Authenticate via API to set up the session
    const loginResponse = await page.context().request.post(
      `${BASE_URL}/api/v1/auth/token`,
      {
        data: {
          email: TEST_EMAIL,
          password: TEST_PASSWORD,
        },
      }
    );

    if (!loginResponse.ok()) {
      throw new Error(`Login failed: ${loginResponse.status()} ${loginResponse.statusText()}`);
    }
    console.log('✓ Authenticated via API');

    // Step 2: Get the API request context (now authenticated with cookies)
    const apiRequest = page.context().request;

    // Step 3: Create project via API
    const project = await createProject(apiRequest, `Test Project ${Date.now()}`);
    console.log(`✓ Created project: ${project.id}`);

    // Step 4: Get available agents
    const agents = await getAgents(apiRequest);
    const agent = agents[0];
    if (!agent) {
      throw new Error('No agents available to create workflow');
    }
    console.log(`✓ Found agent: ${agent.name}`);

    // Step 5: Create workflow via API
    const workflow = await createWorkflow(apiRequest, project.id, `Test Workflow ${Date.now()}`, agent.id);
    console.log(`✓ Created workflow: ${workflow.id}`);

    // Step 6: Navigate to the workflow
    await page.goto(`/app/projects/${project.id}/workflows/${workflow.id}?view=advanced`, {
      waitUntil: 'networkidle',
    });

    // Step 7: Open the Assistant drawer
    console.log('Opening Assistant drawer...');
    const assistantButton = page.locator('button:has-text("Assistant")');
    await assistantButton.waitFor({ state: 'visible', timeout: 10000 });
    await assistantButton.click();

    // Verify drawer opened
    await page.locator('text=Workflow Assistant').waitFor({ state: 'visible', timeout: 5000 });
    console.log('✓ Assistant drawer opened');

    // Step 8: Get initial workflow state
    const initialWorkflow = await getWorkflow(apiRequest, project.id, workflow.id);
    const initialStepCount = initialWorkflow.steps.length;
    console.log(`✓ Initial workflow has ${initialStepCount} steps`);

    // Step 9: Send a chat message with concrete instruction
    console.log('Sending assistant message...');
    const chatInput = page.locator('[data-testid="assistant-thread"] input, [data-testid="assistant-thread"] textarea, [placeholder*="message" i], [placeholder*="Type" i]').first();

    // Wait for the chat input to be available, and for the BIND it implies.
    //
    // `visible` alone is the wrong observation, and the placeholder half of the selector above is
    // why. The composer renders BEFORE a server thread is bound, but with the placeholder "Waiting
    // for the conversation to start…" — which matches none of those alternatives — so this selector
    // goes from 0 matching elements pre-bind to 1 post-bind, and its visibility wait is really a
    // wait for the bind. (The `[data-testid="assistant-thread"] …` alternatives never match at all:
    // the composer lives in `ThreadPrimitive.ViewportFooter`, a sibling of the region that carries
    // that test id — the placeholder alternative is the one doing the work.)
    //
    // The composer is also `disabled` for the whole pre-bind window, because `AssistantDrawer` opens
    // the thread with `POST /assistant/threads` on the way in and every send path is gated on it. So
    // the enabled state is asserted EXPLICITLY: a composer that never becomes enabled then fails
    // here, saying so, instead of surfacing as an action timeout inside `fill()` — which a merely
    // flaky selector also produces, making the two indistinguishable after the fact.
    await chatInput.waitFor({ state: 'visible', timeout: 10_000 });
    await expect(
      chatInput,
      'The composer is gated on a bound server thread, so a never-enabled composer means no thread ' +
        'was ever restored (POST /assistant/threads did not answer for this visit).',
    ).toBeEnabled({ timeout: 15_000 });

    // Type a concrete, structural instruction
    await chatInput.fill('Add a Review step at the end with a minimum score of 8');

    // Send message - look for send button
    const sendButton = page.locator('[aria-label="Send"], button:has-text("Send")').first();
    await sendButton.waitFor({ state: 'visible', timeout: 5000 });
    await sendButton.click();

    console.log('✓ Chat message sent');

    // Step 10: Wait for assistant response (allow up to 60s for LLM response)
    console.log('Waiting for assistant response (up to 60s for streamed LLM)...');
    const assistantThread = page.locator('[data-testid="assistant-thread"]');

    // Wait for the thread to update with a response
    let assistantReplyText: string | null = '';
    const maxWaitTime = 60000; // 60 seconds
    const startTime = Date.now();

    while (Date.now() - startTime < maxWaitTime) {
      assistantReplyText = await assistantThread.textContent();
      // Check if we have more than just the user's message
      if (assistantReplyText && assistantReplyText.length > 100) {
        break;
      }
      await page.waitForTimeout(2000);
    }

    expect(assistantReplyText?.length ?? 0).toBeGreaterThan(0);
    console.log(`✓ Received assistant reply (${assistantReplyText?.length ?? 0} chars)`);

    // Step 11: Assert diff preview appears (hard assertion - no soft fallback)
    console.log('Checking for diff preview...');
    const applyButton = page.locator('[data-testid="apply-button"]');

    // Hard assertion: apply button must be visible (concrete instruction should always produce a diff)
    await expect(applyButton).toBeVisible({ timeout: 15000 });
    console.log('✓ Diff preview and Apply button found');

    // Step 12: Verify workflow is unchanged before apply (read-only proposal proof)
    const preApplyWorkflow = await getWorkflow(apiRequest, project.id, workflow.id);
    expect(preApplyWorkflow.steps.length).toBe(initialStepCount);
    console.log('✓ Workflow unchanged before Apply (via API)');

    // Count apply POSTs from here on (listener registered BEFORE the click).
    let applyPostCount = 0;
    page.on('request', (request) => {
      if (request.method() === 'POST' && request.url().includes('/workflows/assistant/apply')) {
        applyPostCount += 1;
      }
    });

    // Mantine notifications, scoped by their title element.
    const notificationWithTitle = (title: string) =>
      page
        .locator('.mantine-Notification-root')
        .filter({ has: page.locator('.mantine-Notification-title', { hasText: new RegExp(`^${title}$`) }) });
    const failureNotifications = page.locator('.mantine-Notification-root').filter({
      hasText: /Failed to apply|Proposal out of date|Cannot read properties|is not a function|undefined/i,
    });

    // Step 13: Click Apply button
    console.log('Clicking Apply button...');
    await applyButton.click();

    // Step 13a: UI shows the green 'Applied' success notification
    const appliedNotification = notificationWithTitle('Applied').filter({ hasText: 'Assistant changes applied' });
    await expect(appliedNotification).toBeVisible({ timeout: 15000 });
    console.log('✓ "Applied" success notification visible');

    // Step 13b: no error notification (title 'Error' or failure text)
    await expect(notificationWithTitle('Error')).toHaveCount(0);
    await expect(failureNotifications).toHaveCount(0);
    console.log('✓ No error notification visible');

    // Step 13c: diff preview / Apply button gone
    await expect(applyButton).toHaveCount(0, { timeout: 10000 });
    console.log('✓ Diff preview gone after Apply');

    await page.waitForTimeout(2000);

    // Step 14: Verify workflow changed after apply (hard assertion)
    console.log('Verifying workflow changes persisted...');
    const postApplyWorkflow = await getWorkflow(apiRequest, project.id, workflow.id);
    expect(postApplyWorkflow.steps.length).toBeGreaterThan(initialStepCount);
    console.log(`✓ Workflow changed after Apply (now ${postApplyWorkflow.steps.length} steps)`);

    // Step 15: settled UI — still no error, exactly one apply POST
    await expect(notificationWithTitle('Error')).toHaveCount(0);
    await expect(failureNotifications).toHaveCount(0);
    expect(applyPostCount).toBe(1);

    // Step 16: close and reopen the Assistant drawer; the applied proposal must not resurface
    await page.getByRole('button', { name: 'Close assistant' }).click();
    await expect(page.locator('text=Workflow Assistant')).toBeHidden({ timeout: 5000 });
    console.log('✓ Assistant drawer closed');
    await page.getByRole('button', { name: /^Assistant/ }).click();
    await page.locator('text=Workflow Assistant').waitFor({ state: 'visible', timeout: 5000 });
    console.log('✓ Assistant drawer reopened');
    await page.waitForTimeout(1500);
    await expect(applyButton).toHaveCount(0);
    await expect(notificationWithTitle('Error')).toHaveCount(0);
    expect(applyPostCount).toBe(1);
    console.log('✓ Diff preview still gone after reopen; exactly 1 apply POST');
  });

  test('should discard workflow changes without applying', async ({ page }) => {
    // Set a longer timeout for this test (120 seconds for LLM responses)
    test.setTimeout(120000);

    // Step 1: Authenticate via API to set up the session
    const loginResponse = await page.context().request.post(
      `${BASE_URL}/api/v1/auth/token`,
      {
        data: {
          email: TEST_EMAIL,
          password: TEST_PASSWORD,
        },
      }
    );

    if (!loginResponse.ok()) {
      throw new Error(`Login failed: ${loginResponse.status()} ${loginResponse.statusText()}`);
    }
    console.log('✓ Authenticated via API');

    // Step 2: Get the API request context (now authenticated with cookies)
    const apiRequest = page.context().request;

    // Step 3: Create project via API
    const project = await createProject(apiRequest, `Test Project Discard ${Date.now()}`);
    console.log(`✓ Created project: ${project.id}`);

    // Step 4: Get available agents
    const agents = await getAgents(apiRequest);
    const agent = agents[0];
    if (!agent) {
      throw new Error('No agents available');
    }

    // Step 5: Create workflow via API
    const workflow = await createWorkflow(
      apiRequest,
      project.id,
      `Test Workflow Discard ${Date.now()}`,
      agent.id
    );
    console.log(`✓ Created workflow: ${workflow.id}`);

    // Step 6: Navigate to workflow
    await page.goto(`/app/projects/${project.id}/workflows/${workflow.id}?view=advanced`, {
      waitUntil: 'networkidle',
    });

    // Step 7: Get initial workflow state
    const initialWorkflow = await getWorkflow(apiRequest, project.id, workflow.id);
    const initialStepCount = initialWorkflow.steps.length;
    console.log(`✓ Initial workflow has ${initialStepCount} steps`);

    // Step 8: Open Assistant and send message
    const assistantButton = page.locator('button:has-text("Assistant")');
    await assistantButton.waitFor({ state: 'visible', timeout: 10000 });
    await assistantButton.click();
    await page.locator('text=Workflow Assistant').waitFor({ state: 'visible', timeout: 5000 });
    console.log('✓ Assistant drawer opened');

    // Send message with concrete instruction
    const chatInput = page.locator('[data-testid="assistant-thread"] input, [data-testid="assistant-thread"] textarea, [placeholder*="message" i], [placeholder*="Type" i]').first();
    await chatInput.waitFor({ state: 'visible', timeout: 10000 });
    await chatInput.fill('Add a Review step at the end with a minimum score of 8');
    const sendButton = page.locator('[aria-label="Send"], button:has-text("Send")').first();
    await sendButton.waitFor({ state: 'visible', timeout: 5000 });
    await sendButton.click();
    console.log('✓ Message sent');

    // Wait for response
    console.log('Waiting for assistant response...');
    const assistantThread = page.locator('[data-testid="assistant-thread"]');
    let assistantReplyText: string | null = '';
    const maxWaitTime = 60000;
    const startTime = Date.now();

    while (Date.now() - startTime < maxWaitTime) {
      assistantReplyText = await assistantThread.textContent();
      if (assistantReplyText && assistantReplyText.length > 50) {
        break;
      }
      await page.waitForTimeout(2000);
    }

    expect(assistantReplyText?.length ?? 0).toBeGreaterThan(0);
    console.log('✓ Received assistant response');

    // Step 9: Assert discard button exists (hard assertion - no soft fallback)
    const discardButton = page.locator('[data-testid="discard-button"]');

    // Hard assertion: discard button must be visible (concrete instruction should always produce a diff)
    await expect(discardButton).toBeVisible({ timeout: 15000 });
    console.log('✓ Discard button found');

    // Verify workflow unchanged before discard
    const preDiscardWorkflow = await getWorkflow(apiRequest, project.id, workflow.id);
    expect(preDiscardWorkflow.steps.length).toBe(initialStepCount);
    console.log('✓ Workflow unchanged before Discard (via API)');

    // Click discard
    await discardButton.click();
    await page.waitForTimeout(1000);
    console.log('✓ Discard button clicked');

    // Verify workflow still unchanged after discard (hard assertion)
    const postDiscardWorkflow = await getWorkflow(apiRequest, project.id, workflow.id);
    expect(postDiscardWorkflow.steps.length).toBe(initialStepCount);
    console.log('✓ Workflow unchanged after Discard (via API)');
  });

  test('should keep workflow unchanged when navigating away without applying', async ({
    page,
  }) => {
    // Set a longer timeout for this test (120 seconds for LLM responses)
    test.setTimeout(120000);

    // Step 1: Authenticate via API to set up the session
    const loginResponse = await page.context().request.post(
      `${BASE_URL}/api/v1/auth/token`,
      {
        data: {
          email: TEST_EMAIL,
          password: TEST_PASSWORD,
        },
      }
    );

    if (!loginResponse.ok()) {
      throw new Error(`Login failed: ${loginResponse.status()} ${loginResponse.statusText()}`);
    }
    console.log('✓ Authenticated via API');

    // Step 2: Get the API request context (now authenticated with cookies)
    const apiRequest = page.context().request;

    // Step 3: Create project via API
    const project = await createProject(apiRequest, `Test Project Navigate ${Date.now()}`);
    console.log(`✓ Created project: ${project.id}`);

    // Step 4: Get available agents
    const agents = await getAgents(apiRequest);
    const agent = agents[0];
    if (!agent) {
      throw new Error('No agents available');
    }

    // Step 5: Create workflow via API
    const workflow = await createWorkflow(
      apiRequest,
      project.id,
      `Test Workflow Navigate ${Date.now()}`,
      agent.id
    );
    console.log(`✓ Created workflow: ${workflow.id}`);

    // Step 6: Navigate to workflow
    await page.goto(`/app/projects/${project.id}/workflows/${workflow.id}?view=advanced`, {
      waitUntil: 'networkidle',
    });

    // Step 7: Get initial workflow state
    const initialWorkflow = await getWorkflow(apiRequest, project.id, workflow.id);
    const initialStepCount = initialWorkflow.steps.length;
    console.log(`✓ Initial workflow has ${initialStepCount} steps`);

    // Step 8: Open Assistant and send message
    const assistantButton = page.locator('button:has-text("Assistant")');
    await assistantButton.waitFor({ state: 'visible', timeout: 10000 });
    await assistantButton.click();
    await page.locator('text=Workflow Assistant').waitFor({ state: 'visible', timeout: 5000 });
    console.log('✓ Assistant drawer opened');

    // Send message with concrete instruction
    const chatInput = page.locator('[data-testid="assistant-thread"] input, [data-testid="assistant-thread"] textarea, [placeholder*="message" i], [placeholder*="Type" i]').first();
    await chatInput.waitFor({ state: 'visible', timeout: 10000 });
    await chatInput.fill('Add a Review step at the end with a minimum score of 8');
    const sendButton = page.locator('[aria-label="Send"], button:has-text("Send")').first();
    await sendButton.waitFor({ state: 'visible', timeout: 5000 });
    await sendButton.click();
    console.log('✓ Message sent');

    // Wait for response
    console.log('Waiting for assistant response...');
    const assistantThread = page.locator('[data-testid="assistant-thread"]');
    let assistantReplyText: string | null = '';
    const maxWaitTime = 60000;
    const startTime = Date.now();

    while (Date.now() - startTime < maxWaitTime) {
      assistantReplyText = await assistantThread.textContent();
      if (assistantReplyText && assistantReplyText.length > 50) {
        break;
      }
      await page.waitForTimeout(2000);
    }

    expect(assistantReplyText?.length ?? 0).toBeGreaterThan(0);
    console.log('✓ Received assistant response');

    // Step 9: Assert diff preview appears before navigating away (hard assertion)
    const applyButton = page.locator('[data-testid="apply-button"]');
    await expect(applyButton).toBeVisible({ timeout: 15000 });
    console.log('✓ Diff preview and Apply button visible');

    // Step 10: Navigate away without applying
    console.log('Navigating away without applying...');
    await page.goto(`/app/projects/${project.id}`, { waitUntil: 'networkidle' });
    console.log('✓ Navigated away from workflow');

    // Step 11: Navigate back and verify workflow unchanged (hard assertion)
    await page.goto(`/app/projects/${project.id}/workflows/${workflow.id}?view=advanced`, {
      waitUntil: 'networkidle',
    });
    const finalWorkflow = await getWorkflow(apiRequest, project.id, workflow.id);
    expect(finalWorkflow.steps.length).toBe(initialStepCount);
    console.log('✓ Workflow unchanged after navigate-away (via API)');
  });
});
