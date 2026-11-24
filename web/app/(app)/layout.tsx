import { Suspense } from 'react';
import { AdminOnlyNotice } from '@/components/shell/AdminOnlyNotice';
import { ActivityProvider } from '@/lib/activity/ActivityProvider';
import { AssistantShellProvider } from '@/components/assistant/AssistantShellProvider';
import { AppShell } from '@/components/shell/AppShell';
import { ServiceWorkerRegistration } from '@/components/shell/ServiceWorkerRegistration';
import { SwrDefaults } from '@/components/shell/SwrDefaults';
import { TelemetryConsentSurface } from '@/lib/telemetry';
import { authPageConfig } from '@/lib/auth/config';
import { videoEditorEnabled } from '@/lib/editor/config';
import { workflowAppsEnabled } from '@/lib/workflow-apps/config';
import { welcomeFlowEnabled } from '@/lib/onboarding/config';
import { publicApiEnabled } from '@/lib/public-api/config';

/**
 * The runtime video-editor flag is read here, once, on the server, and handed to the shell. The
 * client components that must hide the editor (the nav, a render's "Open in Editor") cannot read
 * the environment themselves — a browser bundle sees only NEXT_PUBLIC_* — so the value is threaded
 * rather than re-read (see components/shell/VideoEditorFlag.tsx).
 */
// The flag is read per request and rendered into the shell, so every route under (app) must be
// rendered per request rather than baked at build time — otherwise the nav on a prerendered page
// keeps whatever the build environment had, and the flag stops being a runtime switch. This is the
// rule the auth pages already follow for their runtime flags (app/(auth)/signup/page.tsx).
export const dynamic = 'force-dynamic';

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <>
      <ServiceWorkerRegistration />
      <Suspense fallback={null}>
        <AdminOnlyNotice />
      </Suspense>
      {/* alpha-launch T-018: the app-side consent popup. Portaled above the shell's chrome, so it is
          mounted here for the layout's lifetime but does not sit in this flow. Renders nothing unless
          the build enables telemetry and no choice has been made yet. */}
      <TelemetryConsentSurface />
      <SwrDefaults>
        <ActivityProvider>
          <AssistantShellProvider>
            <AppShell
              videoEditorEnabled={videoEditorEnabled()}
              workflowAppsEnabled={workflowAppsEnabled()}
              publicApiEnabled={publicApiEnabled()}
              welcomeFlowEnabled={welcomeFlowEnabled()}
              turnstileSiteKey={authPageConfig().turnstileSiteKey}
            >{children}</AppShell>
          </AssistantShellProvider>
        </ActivityProvider>
      </SwrDefaults>
    </>
  );
}
