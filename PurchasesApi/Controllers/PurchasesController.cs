using System.Collections.Generic;
using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EchoApi.Controllers
{
   [ApiController]
   [Route( "[controller]" )]
   public class PurchasesController : ControllerBase
   {
      private readonly ILogger<PurchasesController> _logger;

      public PurchasesController( ILogger<PurchasesController> logger )
      {
         _logger = logger;
      }

      [HttpGet()]
      public List<Purchase> GetPurchases()
      {
         return new List<Purchase> {
            new Purchase()
            {
               BookName = "The Star Wars",
               DateOfPurchase = new System.DateTime(2022,1,1),
               Price = 100.99
            },
            new Purchase()
            {
               BookName = "The Gladiator",
               DateOfPurchase = new System.DateTime(2022,1,1),
               Price = 49.99
            }
         };
      }
   }
}
