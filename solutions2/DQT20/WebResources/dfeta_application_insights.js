var appInsights = window.appInsights || function (a) {
    function b(a) { c[a] = function () { var b = arguments; c.queue.push(function () { c[a].apply(c, b) }) } } var c = { config: a }, d = document, e = window; setTimeout(function () { var b = d.createElement("script"); b.src = a.url || "https://az416426.vo.msecnd.net/scripts/a/ai.0.js", d.getElementsByTagName("script")[0].parentNode.appendChild(b) }); try { c.cookie = d.cookie } catch (a) { } c.queue = []; for (var f = ["Event", "Exception", "Metric", "PageView", "Trace", "Dependency"]; f.length;)b("track" + f.pop()); if (b("setAuthenticatedUserContext"), b("clearAuthenticatedUserContext"), b("startTrackEvent"), b("stopTrackEvent"), b("startTrackPage"), b("stopTrackPage"), b("flush"), !a.disableExceptionTracking) { f = "onerror", b("_" + f); var g = e[f]; e[f] = function (a, b, d, e, h) { var i = g && g(a, b, d, e, h); return !0 !== i && c["_" + f](a, b, d, e, h), i } } return c
}({
    instrumentationKey: "39b62e63-9452-4197-901d-92c96cda8f25"
});

window.appInsights = appInsights, appInsights.queue && 0 === appInsights.queue.length &&
    appInsights.trackPageView(Xrm.Page.data.entity.getEntityName(), location.href, {
        UserName: Xrm.Page.context.getUserName(),
        UserId: Xrm.Page.context.getUserId(),
        RecordGuid: Xrm.Page.data.entity.getId(),
        OperationType: Xrm.Page.ui.getFormType() == 1 ? 'Create' : 'Update',
        FormName: Xrm.Page.ui.formSelector.getCurrentItem().getLabel()
});