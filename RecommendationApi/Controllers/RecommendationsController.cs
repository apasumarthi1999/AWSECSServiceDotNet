using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EchoApi.Controllers
{
   [ApiController]
   [Route( "[controller]" )]
   public class RecommendationsController : ControllerBase
   {
      private readonly ILogger<RecommendationsController> _logger;

      public RecommendationsController( ILogger<RecommendationsController> logger )
      {
         _logger = logger;
      }

      [HttpGet()]
      public List<Recommendation> GetRecommendations()
      {
         return new List<Recommendation> {
            new Recommendation()
            {
               BookName = "The Eternals",
               DateOfPublication = new System.DateTime(2021,12,12),
               Price = 100.99
            },
            new Recommendation()
            {
               BookName = "The Seize",
               DateOfPublication = new System.DateTime(2010,12,12),
               Price = 39.99
            }
         };
      }
   }
}
