import type { MediaClip, EditTimeline } from '@/lib/editing/types';
import { inputProfilesIn, inputStageOf, mediaRefOf, ProxyUrlResolver, stabilizedSourcesIn, type ProxyResolverDeps } from '@/lib/editing/preview';
import { derivativeContentUrl, fetchMediaDerivatives, originalContentUrl, requestConvertedProxy, requestMediaDerivatives } from '@/lib/editing/api/media';
import type { HdrKindLookup } from '@/lib/editing/media/hdr';

/** The proxy resolver of one project, with the converted-proxy request connected (once per source and profile per session). */
export function createWorkspaceProxyResolver(projectId: string): ProxyUrlResolver {
  const deps: ProxyResolverDeps = {
    fetchDerivatives: (mediaRef) => fetchMediaDerivatives(projectId, mediaRef),
    proxyUrl: (row) => derivativeContentUrl(projectId, row),
    originalUrl: (mediaRef) => originalContentUrl(projectId, mediaRef),
    requestStabilized: (mediaRef) => {
      // Fire and forget: a refused request leaves the unstabilised picture playing; the job mark shows a failure from the stream.
      void requestMediaDerivatives(projectId, { mediaRef, kinds: ['stabilizedProxy'], requestedBy: 'stabilize' }).catch(() => undefined);
    },
    requestConverted: (mediaRef, cameraProfile) => {
      // Fire and forget: a refused request leaves the plain proxy playing with the GL input stage.
      void requestConvertedProxy(projectId, mediaRef, cameraProfile).catch(() => undefined);
    },
  };
  return new ProxyUrlResolver(deps);
}

export interface WorkspaceSessionHooks {
  /** The URL a clip plays: the converted proxy when its profile's conversion is ready, else the plain proxy. */
  proxyUrlFor: (clip: MediaClip) => string | null;
  /** What the PICTURE decodes: the stabilised proxy when the clip is stabilised and it is ready, else `proxyUrlFor`. */
  pictureUrlFor: (clip: MediaClip) => string | null;
  /** The compositor's input stage for a clip (null = none: no profile, or HDR). */
  inputStageFor: (clip: MediaClip) => { profile: string; baked: boolean } | null;
}

/** The two clip-level hooks the live session needs for the input stage, built from the resolver and the HDR lookup. */
export function buildSessionHooks(resolver: ProxyUrlResolver, hdrKindFor: HdrKindLookup): WorkspaceSessionHooks {
  return {
    proxyUrlFor: (clip) => resolver.urlFor(mediaRefOf(clip.assetRef), clip.inputProfile),
    pictureUrlFor: (clip) => stabilizedPictureUrl(resolver, clip) ?? resolver.urlFor(mediaRefOf(clip.assetRef), clip.inputProfile),
    inputStageFor: (clip) => {
      const stage = inputStageOf(clip, resolver, Boolean(hdrKindFor(clip)));
      // The stabilised proxy is cut from the source: it carries no camera conversion.
      return stage && stabilizedPictureUrl(resolver, clip) ? { ...stage, baked: false } : stage;
    },
  };
}

/** The stabilised proxy's url for a clip with stabilisation on, once it is listed; null otherwise. */
export function stabilizedPictureUrl(resolver: ProxyUrlResolver, clip: MediaClip): string | null {
  return clip.stabilize === true ? resolver.stabilizedUrlFor(mediaRefOf(clip.assetRef)) : null;
}

/** Asks (once per session) for the stabilised proxy of every source a stabilised clip uses. */
export function wantDocumentStabilized(resolver: ProxyUrlResolver, doc: EditTimeline): void {
  resolver.wantStabilized(stabilizedSourcesIn(doc).keys());
}

/** Asks for the converted proxy of every (source, profile) the document uses, skipping HDR sources (the profile is ignored there). */
export function wantDocumentProfiles(resolver: ProxyUrlResolver, doc: EditTimeline, isHdrSource: (mediaRef: string) => boolean): void {
  resolver.wantProfiles(inputProfilesIn(doc).filter((p) => !isHdrSource(p.mediaRef)));
}
