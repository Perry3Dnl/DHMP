namespace DHMP.RawIpv6;

internal static class DhmpPathMtuSearch
{
    public static async Task<DhmpPathMtuDiscoveryResult> RunAsync(
        DhmpPathMtuDiscoveryOptions options,
        Func<int, CancellationToken, ValueTask<bool>> probePathMtuOnce,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probePathMtuOnce);

        int transmissions = 0;
        int basePathMtu = DhmpIpv6PathBudget.MinimumIpv6Mtu;

        async ValueTask<bool> ConfirmAsync(int pathMtu)
        {
            for (int attempt = 0;
                 attempt < options.MaximumProbeAttempts;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                transmissions++;

                if (await probePathMtuOnce(
                        pathMtu,
                        cancellationToken)
                    .ConfigureAwait(false))
                    return true;
            }

            return false;
        }

        bool baseConfirmed =
            await ConfirmAsync(basePathMtu)
                .ConfigureAwait(false);

        if (!baseConfirmed)
        {
            return new DhmpPathMtuDiscoveryResult(
                DhmpPathMtuDiscoveryState.Error,
                new DhmpIpv6PathBudget(
                    basePathMtu,
                    options.AdditionalIpv6HeaderBytes),
                baseConfirmed: false,
                reachedConfiguredMaximum: false,
                probeTransmissions: transmissions);
        }

        if (options.MaximumPathMtu == basePathMtu)
        {
            return Complete(
                basePathMtu,
                reachedMaximum: true,
                transmissions,
                options);
        }

        if (await ConfirmAsync(options.MaximumPathMtu)
                .ConfigureAwait(false))
        {
            return Complete(
                options.MaximumPathMtu,
                reachedMaximum: true,
                transmissions,
                options);
        }

        int confirmed = basePathMtu;
        int upperExclusive = options.MaximumPathMtu;

        while (upperExclusive - confirmed >
               options.MinimumSearchGainBytes)
        {
            int candidate =
                confirmed +
                ((upperExclusive - confirmed) / 2);

            if (candidate <= confirmed)
                break;

            if (await ConfirmAsync(candidate)
                    .ConfigureAwait(false))
            {
                confirmed = candidate;
            }
            else
            {
                upperExclusive = candidate;
            }
        }

        return Complete(
            confirmed,
            reachedMaximum: false,
            transmissions,
            options);
    }

    private static DhmpPathMtuDiscoveryResult Complete(
        int confirmedPathMtu,
        bool reachedMaximum,
        int transmissions,
        DhmpPathMtuDiscoveryOptions options)
        => new(
            DhmpPathMtuDiscoveryState.SearchComplete,
            new DhmpIpv6PathBudget(
                confirmedPathMtu,
                options.AdditionalIpv6HeaderBytes),
            baseConfirmed: true,
            reachedConfiguredMaximum: reachedMaximum,
            probeTransmissions: transmissions);
}
