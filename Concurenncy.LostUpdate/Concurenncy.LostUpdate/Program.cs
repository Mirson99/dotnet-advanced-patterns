using Concurenncy.LostUpdate;
using Microsoft.EntityFrameworkCore;

await using var db = new LabDbContext();

Console.WriteLine($"Migrating {db.Database.GetDbConnection().DataSource} / {db.Database.GetDbConnection().Database} ...");

await db.Database.MigrateAsync();

using (var initDb = new LabDbContext())
{
    await initDb.Database.EnsureCreatedAsync();

    var account = await initDb.BankAccounts.FirstAsync(a => a.Id == 1);
    account.Amount = 100;
    await initDb.SaveChangesAsync();

    Console.WriteLine($"[START] Konto ID: {account.Id}, Saldo początkowe: {account.Amount} zł\n");
}

await pesimisticSolution();

// 4. Weryfikacja wyniku w bazie
using (var verifyDb = new LabDbContext())
{
    var finalAccount = await verifyDb.BankAccounts.AsNoTracking().FirstAsync(a => a.Id == 1);

    Console.WriteLine("\n-------------------------------------------");
    Console.WriteLine($"[KONIEC] Oczekiwane saldo: 0 zł");
    Console.WriteLine($"[KONIEC] Faktyczne saldo w bazie: {finalAccount.Amount} zł");
    Console.WriteLine("-------------------------------------------");
}


async Task optimisticSolution()
{
    var tasks = Enumerable.Range(1, 10).Select(async threadId =>
    {
        const int maxRetries = 10;
        int retryCount = 0;

        while (retryCount < maxRetries)
        {
            using var db = new LabDbContext();

            try
            {
                // 1. Pobieramy rekord (wraz z jego aktualnym RowVersion)
                var account = await db.BankAccounts.FirstAsync(a => a.Id == 1);

                await Task.Delay(10); // Symulacja pracy

                // 2. Walidacja biznesowa na świeżych danych
                if (account.Amount >= 10)
                {
                    account.Amount -= 10;
                
                    // 3. Próba zapisu
                    await db.SaveChangesAsync();
                
                    Console.WriteLine($"Wątek {threadId:D2}: Pobrano 10 zł (próba {retryCount + 1}). Zapis udany.");
                    return; // Sukces, kończymy zadanie
                }
                else
                {
                    Console.WriteLine($"Wątek {threadId:D2}: Odrzucono - brak środków po odświeżeniu stanu konta.");
                    return; // Brak środków, nie ma sensu ponawiać
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                retryCount++;
                Console.WriteLine($"[KOLIZJA] Wątek {threadId:D2} wykrył zmianę w bazie. Ponowienie nr {retryCount}...");
            
                // Krótkie losowe opóźnienie (jitter), żeby wątki nie zderzały się ciągle w tym samym momencie
                await Task.Delay(Random.Shared.Next(5, 20));
            }
        }

        Console.WriteLine($"Wątek {threadId:D2}: Przekroczono limit prób!");
    });

    await Task.WhenAll(tasks);
}


async Task pesimisticSolution()
{
    var tasks = Enumerable.Range(1, 10).Select(async threadId =>
    {
        using var db = new LabDbContext();

        // 1. Kluczowe: Blokada pesymistyczna MUSI żyć wewnątrz transakcji.
        // Trwa tak długo, aż zrobimy Commit() lub Rollback().
        await using var transaction = await db.Database.BeginTransactionAsync();

        try
        {
            // 2. Pobieramy rekord z podpowiedzią WITH (UPDLOCK, ROWLOCK).
            // Pierwszy wątek przechodzi od razu. Pozostałe 9 wątków baza "zamraża" na tej linijce!
            var account = await db.BankAccounts
                .FromSqlInterpolated($"SELECT * FROM BankAccounts WITH (UPDLOCK, ROWLOCK) WHERE Id = 1")
                .FirstAsync();

            // Symulacja pracy biznesowej
            await Task.Delay(10);

            // 3. Sprawdzamy warunek na zablokowanym rekordzie
            if (account.Amount >= 10)
            {
                account.Amount -= 10;
                await db.SaveChangesAsync();

                // 4. Zatwierdzamy transakcję - w tym momencie baza zwalnia blokadę 
                // i wpuszcza KOLEJNY wątek z kolejki!
                await transaction.CommitAsync();

                Console.WriteLine($"Wątek {threadId:D2}: Pobrano 10 zł. Zapis udany.");
            }
            else
            {
                // Brak środków - wycofujemy i zwalniamy blokadę
                await transaction.RollbackAsync();
                Console.WriteLine($"Wątek {threadId:D2}: Brak środków!");
            }
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            Console.WriteLine($"Wątek {threadId:D2} błąd: {ex.Message}");
        }
    });

    await Task.WhenAll(tasks);
}